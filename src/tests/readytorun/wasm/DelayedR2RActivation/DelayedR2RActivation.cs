// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace System.Runtime
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class BypassReadyToRunAttribute : Attribute
    {
    }
}

public class DelayedR2RActivation
{
    private const int PrefersInterpreterEntryPoint = 0x4;
    private const int PendingAuxiliaryModule = 0x10;
    private static readonly int s_activationCallCount =
        Environment.GetEnvironmentVariable("DOTNET_R2RDelayedActivationCallCount") is string value
            ? int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;
    private static Action s_atRecursiveBottom;

    [Fact]
    [BypassReadyToRun]
    [RuntimeAsyncMethodGeneration(false)]
    public static async Task ActivatesCompositeImageAsync()
    {
        IntPtr suspendedEntryPoint = PrepareMethod(nameof(SuspendedTargetAsync));
        IntPtr repeatedlySuspendedEntryPoint = PrepareMethod(nameof(RepeatedlySuspendedTargetAsync));
        IntPtr[] entryPoints =
        [
            PrepareMethod(nameof(DirectTarget)),
            PrepareMethod(nameof(DelegateTarget)),
            PrepareMethod(nameof(ReflectionTarget)),
            PrepareMethod(typeof(DelayedR2RActivation).GetMethod(nameof(GenericTarget)).MakeGenericMethod(typeof(int))),
            PrepareMethod(nameof(GenericCaller)),
            PrepareMethod(nameof(RecursiveTarget)),
            PrepareMethod(nameof(ExceptionTarget)),
            PrepareMethod(nameof(CompiledCaller)),
            PrepareMethod(nameof(NativeToInterpreterTarget)),
            PrepareMethod(typeof(VirtualTarget).GetMethod(nameof(VirtualTarget.Invoke))),
            PrepareMethod(typeof(CovariantTarget).GetMethod(nameof(CovariantTarget.Invoke))),
            suspendedEntryPoint,
            repeatedlySuspendedEntryPoint,
            PrepareMethod(nameof(CrossModuleGenericCaller)),
            PrepareMethod(typeof(List<CrossModuleValue>).GetMethod("AddWithResize", BindingFlags.Instance | BindingFlags.NonPublic))
        ];
        IntPtr untouchedEntryPoint = PrepareMethod(nameof(UntouchedTarget));
        IntPtr interpreterOnlyEntryPoint = PrepareMethod(nameof(InterpreterOnlyTarget));
        bool untouchedWasPending = IsPendingAuxiliaryModule(untouchedEntryPoint);

        if (s_activationCallCount == 0 || s_activationCallCount >= 1000)
        {
            foreach (IntPtr entryPoint in entryPoints)
            {
                Assert.Equal(s_activationCallCount != 0, PrefersInterpreter(entryPoint));
                Assert.Equal(s_activationCallCount != 0, IsPendingAuxiliaryModule(entryPoint));
            }
            Assert.Equal(s_activationCallCount != 0, untouchedWasPending);
        }

        VerifyCalls(1, callCompiledTarget: false);

        object value = new();
        TaskCompletionSource<int> originalSource = new();
        Task<object> original = SuspendedTargetAsync(originalSource.Task, value);
        Assert.False(original.IsCompleted);

        int suspensionCount = 3;
        TaskCompletionSource<int>[] sources = new TaskCompletionSource<int>[suspensionCount];
        Task<int>[] pending = new Task<int>[suspensionCount];
        for (int index = 0; index < suspensionCount; index++)
        {
            sources[index] = new();
            pending[index] = sources[index].Task;
        }

        long[] counts = new long[3];
        Task<object> suspended = RepeatedlySuspendedTargetAsync(pending, value, counts);
        if (s_activationCallCount >= 1000)
        {
            // Both continuations must still be interpreted so they resume after their methods are published.
            Assert.True(IsPendingAuxiliaryModule(suspendedEntryPoint));
            Assert.True(IsPendingAuxiliaryModule(repeatedlySuspendedEntryPoint));
        }

        s_atRecursiveBottom = () =>
        {
            s_atRecursiveBottom = null;
            for (int invocation = 0;
                invocation < s_activationCallCount && IsPendingAuxiliaryModule(entryPoints[0]);
                invocation++)
            {
                Assert.Equal(invocation * 7 + 13, DirectTarget(invocation));
            }

            if (s_activationCallCount >= 1000)
            {
                Assert.False(IsPendingAuxiliaryModule(entryPoints[0]));
                Assert.True(IsPendingAuxiliaryModule(suspendedEntryPoint));
                Assert.True(IsPendingAuxiliaryModule(repeatedlySuspendedEntryPoint));
            }

            for (int index = 0; index < suspensionCount; index++)
            {
                Assert.False(suspended.IsCompleted);
                sources[index].SetResult(index + 1);
                Assert.Equal(((long)index + 1) * (index + 2) / 2, counts[1]);
                if (index == 0 || index == suspensionCount - 1)
                {
                    GC.Collect();
                }
            }

            Assert.Equal(0, RecursiveTarget(0));
            Task<object> completed = SuspendedTargetAsync(Task.FromResult(37), value);
            Assert.True(completed.IsCompletedSuccessfully);
            Assert.Same(value, completed.GetAwaiter().GetResult());
        };
        Assert.Equal(10, RecursiveTarget(4));
        Assert.Null(s_atRecursiveBottom);

        Assert.Same(value, await suspended);
        Assert.Equal(1L, counts[0]);
        Assert.Equal(1L, counts[2]);
        Assert.Same(value, await RepeatedlySuspendedTargetAsync(Array.Empty<Task<int>>(), value, new long[3]));

        originalSource.SetResult(43);
        Assert.Same(value, await original);

        Assert.Equal(untouchedWasPending, IsPendingAuxiliaryModule(untouchedEntryPoint));
        Assert.Equal(untouchedWasPending, PrefersInterpreter(untouchedEntryPoint));
        Assert.Equal(36, UntouchedTarget(5));
        Assert.False(IsPendingAuxiliaryModule(untouchedEntryPoint));
        Assert.False(PrefersInterpreter(untouchedEntryPoint));

        IntPtr lateEntryPoint = PrepareMethod(nameof(LatePreparedTarget));
        Assert.False(IsPendingAuxiliaryModule(lateEntryPoint));
        Assert.False(PrefersInterpreter(lateEntryPoint));
        Assert.Equal(46, LatePreparedTarget(5));

        VerifyCalls(7, callCompiledTarget: true);
        foreach (IntPtr entryPoint in entryPoints)
        {
            Assert.False(IsPendingAuxiliaryModule(entryPoint));
            Assert.False(PrefersInterpreter(entryPoint));
        }

        for (int invocation = 0; invocation < 3; invocation++)
        {
            Assert.Equal(invocation + 11, InterpreterOnlyTarget(invocation));
            Assert.True(PrefersInterpreter(interpreterOnlyEntryPoint));
            Assert.False(IsPendingAuxiliaryModule(interpreterOnlyEntryPoint));
        }
    }

    [BypassReadyToRun]
    private static IntPtr PrepareMethod(string methodName) =>
        PrepareMethod(typeof(DelayedR2RActivation).GetMethod(methodName));

    [BypassReadyToRun]
    private static IntPtr PrepareMethod(MethodInfo method)
    {
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        return method.MethodHandle.GetFunctionPointer();
    }

    [BypassReadyToRun]
    private static void VerifyCalls(int value, bool callCompiledTarget)
    {
        Func<int, int> callback = DelegateTarget;
        Assert.Equal(value * 7 + 13, DirectTarget(value));
        Assert.Equal(value * 7 + 13, callback(value));
        Assert.Equal(value * 7 + 13,
            (int)typeof(DelayedR2RActivation).GetMethod(nameof(ReflectionTarget)).Invoke(null, new object[] { value }));
        Assert.Equal(value * 7 + 13, GenericTarget<int>(value));
        // The compiled caller roots both generic bodies; interpreter-only callers do not.
        Assert.Equal(2 * (value * 7 + 13), GenericCaller(value));
        Assert.Equal(5, CrossModuleGenericCaller());
        object marker = new();
        Assert.Same(marker, ExceptionTarget(marker));
        Assert.Equal(callCompiledTarget ? value + 17 : value, CompiledCaller(callCompiledTarget, value));
        VirtualBase virtualTarget = new VirtualTarget();
        Assert.Equal(value + 23, virtualTarget.Invoke(value));
        Assert.Equal(value + 23, ((IVirtualTarget)virtualTarget).Invoke(value));
        CovariantBase covariantTarget = new CovariantTarget();
        Assert.Equal("activated", covariantTarget.Invoke());
    }

    [BypassReadyToRun]
    private static bool IsPendingAuxiliaryModule(IntPtr entryPoint)
    {
        return (Marshal.ReadInt32(entryPoint, 3 * IntPtr.Size) & PendingAuxiliaryModule) != 0;
    }

    [BypassReadyToRun]
    private static bool PrefersInterpreter(IntPtr entryPoint)
    {
        // PortableEntryPoint stores its flags after the code, MethodDesc, and interpreter-data pointers.
        return (Marshal.ReadInt32(entryPoint, 3 * IntPtr.Size) & PrefersInterpreterEntryPoint) != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int DirectTarget(int value) => value * 7 + 13;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int DelegateTarget(int value) => value * 7 + 13;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReflectionTarget(int value) => value * 7 + 13;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GenericTarget<T>(int value) => value * 7 + 13;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GenericCaller(int value) => GenericTarget<int>(value) + GenericTarget<string>(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int RecursiveTarget(int depth)
    {
        if (depth == 0)
        {
            s_atRecursiveBottom?.Invoke();
            return 0;
        }

        return depth + RecursiveTarget(depth - 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CompiledCaller(bool callTarget, int value) => callTarget ? NativeToInterpreterTarget(value) : value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NativeToInterpreterTarget(int value) => value + 17;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int UntouchedTarget(int value) => value + 31;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int LatePreparedTarget(int value) => value + 41;

    public struct CrossModuleValue
    {
        public int Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CrossModuleGenericCaller()
    {
        List<CrossModuleValue> values = new();
        values.Add(new CrossModuleValue { Value = 5 });
        return values[0].Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<object> SuspendedTargetAsync(Task<int> pending, object value)
    {
        await pending;
        GC.KeepAlive(value);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<object> RepeatedlySuspendedTargetAsync(Task<int>[] pending, object value, long[] counts)
    {
        counts[0]++;
        try
        {
            for (int index = 0; index < pending.Length; index++)
            {
                counts[1] += await pending[index];
            }

            return value;
        }
        finally
        {
            counts[2]++;
            GC.KeepAlive(value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object ExceptionTarget(object value)
    {
        try
        {
            throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
            return value;
        }
        finally
        {
            GC.KeepAlive(value);
        }
    }

    [BypassReadyToRun]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int InterpreterOnlyTarget(int value) => value + 11;

    public interface IVirtualTarget
    {
        int Invoke(int value);
    }

    public class VirtualBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual int Invoke(int value) => -1;
    }

    public sealed class VirtualTarget : VirtualBase, IVirtualTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override int Invoke(int value) => value + 23;
    }

    public class CovariantBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual object Invoke() => null;
    }

    public sealed class CovariantTarget : CovariantBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string Invoke() => "activated";
    }
}
