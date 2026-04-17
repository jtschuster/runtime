// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using ILCompiler.Reflection.ReadyToRun.Assertions;

using Internal.ReadyToRunConstants;

using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// xUnit-friendly thin wrapper around the structural <see cref="R2RAssertions"/> library.
/// Each helper converts a <c>HasX(..., out string reason)</c> predicate into an xUnit assertion.
/// </summary>
internal static class R2RAssert
{
    public static void HasManifestRef(R2RAssertions r2r, string assemblyName)
        => Assert.True(r2r.HasManifestRef(assemblyName, out string reason), reason);

    public static void HasInlinedMethod(R2RAssertions r2r, string inlinerMethodName, string inlineeMethodName)
        => Assert.True(r2r.HasInlinedMethod(inlinerMethodName, inlineeMethodName, out string reason), reason);

    public static void HasCrossModuleInlinedMethod(R2RAssertions r2r, string inlinerMethodName, string inlineeMethodName)
        => Assert.True(r2r.HasCrossModuleInlinedMethod(inlinerMethodName, inlineeMethodName, out string reason), reason);

    public static void HasCrossModuleInliners(R2RAssertions r2r, string inlineeMethodName, params string[] expectedInlinerNames)
        => Assert.True(r2r.HasCrossModuleInliners(inlineeMethodName, expectedInlinerNames, out string reason), reason);

    public static void HasCrossModuleInliningInfo(R2RAssertions r2r)
        => Assert.True(r2r.HasCrossModuleInliningInfo(out string reason), reason);

    public static void HasAsyncVariant(R2RAssertions r2r, string methodName)
        => Assert.True(r2r.HasAsyncVariant(methodName, out string reason), reason);

    public static void HasResumptionStub(R2RAssertions r2r, string methodName)
        => Assert.True(r2r.HasResumptionStub(methodName, out string reason), reason);

    public static void HasContinuationLayout(R2RAssertions r2r)
        => Assert.True(r2r.HasContinuationLayout(out string reason), reason);

    public static void HasContinuationLayout(R2RAssertions r2r, string methodName)
        => Assert.True(r2r.HasContinuationLayout(methodName, out string reason), reason);

    public static void HasResumptionStubFixup(R2RAssertions r2r)
        => Assert.True(r2r.HasResumptionStubFixup(out string reason), reason);

    public static void HasResumptionStubFixup(R2RAssertions r2r, string methodName)
        => Assert.True(r2r.HasResumptionStubFixup(methodName, out string reason), reason);

    public static void HasFixupKind(R2RAssertions r2r, ReadyToRunFixupKind kind)
        => Assert.True(r2r.HasFixupKind(kind, out string reason), reason);

    public static void HasFixupKindOnMethod(R2RAssertions r2r, ReadyToRunFixupKind kind, string methodName)
        => Assert.True(r2r.HasFixupKindOnMethod(kind, methodName, out string reason), reason);
}
