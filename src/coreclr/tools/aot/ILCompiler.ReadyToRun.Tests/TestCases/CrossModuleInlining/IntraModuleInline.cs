// Test fixture for the Structural reader's CrossModuleInlineInfo moduleIndex
// inheritance bug (B2). When this assembly is built composite with
// --opt-cross-module on every input, the intra-module inline of Helper into
// TestHelper is recorded in CrossModuleInlineInfo with InlinerRidHasModule
// clear, exercising the inheritance code path in the reader.
//
// CrossModuleCompileable requires generics (see ReadyToRunCompilationModuleGroupBase
// CrossModuleCompileableUncached: rejects non-generic methods), so both the inliner
// and the inlinee here are generic methods on a generic type.
using System;
using System.Runtime.CompilerServices;

public static class IntraModuleInline<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Helper(T x) => x;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T TestHelper(T x) => Helper(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HelperInt(T x, int y) => y * 2;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int TestHelperInt(T x, int y) => HelperInt(x, y);
}

public static class IntraModuleEntry
{
    // Force several instantiations so crossgen2 actually compiles the intra-module
    // generic inline calls under --opt-cross-module.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        string s = IntraModuleInline<string>.TestHelper("x");
        object o = IntraModuleInline<object>.TestHelper(new object());
        int i = IntraModuleInline<string>.TestHelperInt("y", 7);
        int j = IntraModuleInline<object>.TestHelperInt(new object(), 11);
        return s.Length + i + j + (o != null ? 1 : 0) + InlineableLib.GetValue();
    }
}

