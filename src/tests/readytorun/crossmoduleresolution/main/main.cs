// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class Assert
{
    public static bool HasAssertFired;

    public static void AreEqual(object actual, object expected)
    {
        if (!(actual is null && expected is null) && !actual.Equals(expected))
        {
            Console.WriteLine("Not equal!");
            Console.WriteLine("actual   = " + actual.ToString());
            Console.WriteLine("expected = " + expected.ToString());
            HasAssertFired = true;
        }
    }
}

class CrossModuleImpl : AssemblyC.ICrossModule
{
    public int DoWork() => 42;
}

class Program
{
    // --- Version Bubble Tests (B) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_VersionBubble() => new AssemblyB.BType().Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestMethodCall_VersionBubble() => AssemblyB.BClass.StaticMethod();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_VersionBubble() => AssemblyB.BClass.StaticField;

    // --- Cross-Module-Only Tests (C → inlined into main) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_CrossModuleOwn() => AssemblyC.CClass.UseOwnType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_Transitive() => AssemblyC.CClass.UseDType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestMethodCall_Transitive() => AssemblyC.CClass.CallDMethod();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_Transitive() => AssemblyC.CClass.ReadDField();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestNestedType_External() => AssemblyC.CClass.UseNestedType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string TestTypeForwarder() => AssemblyC.CClass.UseForwardedType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_MixedOrigin() => AssemblyC.CClass.UseGenericWithDType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_CoreLib() => AssemblyC.CClass.UseCoreLibGeneric();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_CrossModuleDefinition() => AssemblyC.CGeneric<int>.GetCount();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_CrossModule() => AssemblyC.CClass.StaticField;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestInterfaceDispatch_CrossModule()
    {
        AssemblyC.ICrossModule impl = new CrossModuleImpl();
        return impl.DoWork();
    }

    static void RunAllTests()
    {
        // Version bubble tests
        Assert.AreEqual(TestTypeRef_VersionBubble(), 7);
        Assert.AreEqual(TestMethodCall_VersionBubble(), 77);
        Assert.AreEqual(TestFieldAccess_VersionBubble(), 777);

        // Cross-module-only tests (C's AggressiveInlining methods)
        Assert.AreEqual(TestTypeRef_CrossModuleOwn(), 3);
        Assert.AreEqual(TestTypeRef_Transitive(), 42);
        Assert.AreEqual(TestMethodCall_Transitive(), 101);
        Assert.AreEqual(TestFieldAccess_Transitive(), 100);
        Assert.AreEqual(TestNestedType_External(), 99);
        Assert.AreEqual(TestTypeForwarder(), "forwarded");
        Assert.AreEqual(TestGeneric_MixedOrigin(), 42);
        Assert.AreEqual(TestGeneric_CoreLib(), 3);
        Assert.AreEqual(TestGeneric_CrossModuleDefinition(), 1);
        Assert.AreEqual(TestFieldAccess_CrossModule(), 50);
        Assert.AreEqual(TestInterfaceDispatch_CrossModule(), 42);
    }

    public static int Main()
    {
        // Run all tests 3x to exercise both slow and fast paths
        for (int i = 0; i < 3; i++)
            RunAllTests();

        if (!Assert.HasAssertFired)
            Console.WriteLine("PASSED");
        else
            Console.WriteLine("FAILED");

        return Assert.HasAssertFired ? 1 : 100;
    }
}
