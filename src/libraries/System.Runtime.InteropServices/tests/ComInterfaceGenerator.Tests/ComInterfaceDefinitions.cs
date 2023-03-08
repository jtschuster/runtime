// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Xunit;
using Xunit.Sdk;

namespace ComInterfaceGenerator.Tests;

[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2c3f9903-b586-46b1-881b-adfce9af47b1")]
public partial interface IComInterface
{
    void Method1();
    int Method2();
}

public class ImplIComInterface : IComInterface
{
    int _data = 0;
    public void Method1() => _data = 2;
    public int Method2() => _data;
}
internal sealed partial class MyGeneratedComWrappers : ComWrappers
{
    protected override unsafe ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count) => throw new NotImplementedException();
    protected override object? CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotImplementedException();
    protected override void ReleaseObjects(IEnumerable objects) => throw new NotImplementedException();
}


public class Tests
{
    [Fact]
    public unsafe void UseGeneratedComInterface()
    {
        var details = typeof(IComInterface).GetCustomAttributes().Where(attr => attr.GetType().Name == "IUnknownDerivedAttribute`2").Single();
        var createManagedVirtualFunctionTable = details.GetType().GenericTypeArguments[1].GetRuntimeMethods().Where(x => x.Name == "CreateManagedVirtualFunctionTable").Single(); // details.GetType().GenericTypeArguments[1].GetRuntimeMethod("CreateManagedVirtualFunctionTable", new Type[] { });
        var ret = createManagedVirtualFunctionTable.Invoke(null, null);
        var vtable = (void**)Pointer.Unbox(createManagedVirtualFunctionTable.Invoke(null, null));

        var test = new ImplIComInterface();

        MyGeneratedComWrappers.TryGetComInstance(test, out var asdf);
        //GCHandle gcHandle = GCHandle.Alloc(test, GCHandleType.WeakTrackResurrection);
        //IntPtr ptr = Marshal.ReadIntPtr(GCHandle.ToIntPtr(gcHandle));
        //if (!MyGeneratedComWrappers.TryGetComInstance(test, out var ptr))
        //    throw new FailException("Didn't work");
        var ptr = Unsafe.AsPointer(ref test);
        var data = ((delegate* unmanaged<void*, int>)(vtable[4]))((void*)ptr);
        Assert.Equal(0, data);
        ((delegate* unmanaged<void*, void>)(vtable[3]))((void*)ptr);
        Assert.Equal(2, test.Method2());
        var data2 = ((delegate* unmanaged<void*, int>)(vtable[4]))((void*)ptr);
        Assert.Equal(2, data2);


    }
}
