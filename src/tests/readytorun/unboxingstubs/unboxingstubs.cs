// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavioral test for precompiled unboxing stubs. The .csproj crossgen2's this assembly, so the
// value-type virtual/interface instance methods below are dispatched through the precompiled
// unboxing stubs emitted into the R2R image. Each struct carries instance fields, so a stub with a
// wrong `this` adjustment (e.g. not skipping the MethodTable*) would read garbage and fail the
// checks. Covers the riskiest shapes: boxed virtual override, implicit + explicit interface
// dispatch, a return-buffer-returning interface method (retbuf vs `this` register ordering), and a
// closed delegate over a value-type instance method, and a shared-generic value type (instantiated
// over a reference type, so dispatch flows through a shared-generic unboxing stub that also loads the
// generic context from the boxed object).
using System;
using System.Runtime.CompilerServices;

public interface IValue
{
    int GetValue();
}

public interface IExplicitValue
{
    int GetExplicitValue();
}

// Large enough to be returned via a hidden return buffer on the major ABIs.
public struct BigResult
{
    public long A;
    public long B;
    public long C;
    public long D;
}

public interface IBigReturner
{
    BigResult ComputeBig();
}

public struct ValueStruct : IValue, IExplicitValue
{
    public int X;
    public int Y;

    public override string ToString() => $"VS({X},{Y})";
    public override int GetHashCode() => X * 31 + Y;
    public override bool Equals(object obj) => obj is ValueStruct other && other.X == X && other.Y == Y;

    public int GetValue() => X * 10 + Y;
    int IExplicitValue.GetExplicitValue() => X * 100 + Y;
}

public struct BigStruct : IBigReturner
{
    public long Seed;

    public BigResult ComputeBig() => new BigResult { A = Seed, B = Seed + 1, C = Seed + 2, D = Seed + 3 };
}

public interface IGenericValue<T>
{
    T Produce();
    string Describe();
}

// Shared-generic value type: instantiated over a reference-type argument (string) it compiles to the
// canonical __Canon form, so boxed dispatch goes through a *shared-generic* unboxing stub
// (GenericUnboxingThunk). Produce() returns T; Describe() reads typeof(T), which forces the stub to
// load the generic context from the boxed object (RequiresInstMethodTableArg) — exercising the
// context-loading path, not just the plain `this` adjustment.
public struct GenericStruct<T> : IGenericValue<T>
{
    public T Field;
    public int Tag;

    public T Produce() => Field;
    public string Describe() => $"GS<{typeof(T).Name}>:{Tag}";
}

public static class UnboxingStubsTest
{
    private static bool s_failed;

    private static void Check(string name, long expected, long actual)
    {
        if (expected != actual)
        {
            Console.WriteLine($"  FAILED: {name} — expected {expected}, got {actual}");
            s_failed = true;
        }
    }

    private static void Check(string name, string expected, string actual)
    {
        if (expected != actual)
        {
            Console.WriteLine($"  FAILED: {name} — expected '{expected}', got '{actual}'");
            s_failed = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallToString(object boxed) => boxed.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CallInterface(IValue v) => v.GetValue();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CallExplicitInterface(IExplicitValue v) => v.GetExplicitValue();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BigResult CallBig(IBigReturner b) => b.ComputeBig();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallGenericProduce(IGenericValue<string> g) => g.Produce();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallGenericDescribe(IGenericValue<string> g) => g.Describe();

    public static int Main()
    {
        var vs = new ValueStruct { X = 3, Y = 4 };

        // Boxed virtual overrides (Object methods).
        Check("ToString", "VS(3,4)", CallToString(vs));
        Check("GetHashCode", 3 * 31 + 4, ((object)vs).GetHashCode());
        Check("Equals.True", 1, ((object)vs).Equals(new ValueStruct { X = 3, Y = 4 }) ? 1 : 0);
        Check("Equals.False", 0, ((object)vs).Equals(new ValueStruct { X = 9, Y = 9 }) ? 1 : 0);

        // Implicit interface dispatch on a boxed value type.
        Check("ImplicitInterface", 34, CallInterface(vs));

        // Explicit interface dispatch on a boxed value type.
        Check("ExplicitInterface", 304, CallExplicitInterface(vs));

        // Return-buffer-returning interface method on a boxed value type.
        BigResult big = CallBig(new BigStruct { Seed = 100 });
        Check("Retbuf.A", 100, big.A);
        Check("Retbuf.B", 101, big.B);
        Check("Retbuf.C", 102, big.C);
        Check("Retbuf.D", 103, big.D);

        // Closed delegate over a value-type instance method (target is a boxed copy).
        Func<int> getter = new ValueStruct { X = 5, Y = 6 }.GetValue;
        Check("Delegate", 56, getter());

        // Shared-generic value-type unboxing stubs (reference-type arg => __Canon shared code).
        IGenericValue<string> gv = new GenericStruct<string> { Field = "hello", Tag = 7 };
        Check("Generic.Produce", "hello", CallGenericProduce(gv));
        Check("Generic.Describe", "GS<String>:7", CallGenericDescribe(gv));

        if (s_failed)
        {
            Console.WriteLine("FAILED");
            return 1;
        }

        Console.WriteLine("PASSED");
        return 100;
    }
}
