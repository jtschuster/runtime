// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.ReadyToRun.Tests.TestCasesRunner;
using Internal.ReadyToRunConstants;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCases;

/// <summary>
/// xUnit test suites for R2R cross-module resolution tests.
/// Each test method builds assemblies with Roslyn, crossgen2's them, and validates the R2R output.
/// </summary>
public class R2RTestSuites
{
    private static readonly KeyValuePair<string, string> RuntimeAsyncFeature = new("runtime-async", "on");

    [Fact]
    public void BasicCrossModuleInlining()
    {
        var expectations = new R2RExpectations();
        expectations.ExpectedManifestRefs.Add("InlineableLib");
        expectations.ExpectedInlinedMethods.Add(new ExpectedInlinedMethod("GetValue"));
        expectations.ExpectedInlinedMethods.Add(new ExpectedInlinedMethod("GetString"));
        expectations.Crossgen2Options.Add("--opt-cross-module:InlineableLib");

        var testCase = new R2RTestCase
        {
            Name = "BasicCrossModuleInlining",
            MainSourceResourceName = "CrossModuleInlining/BasicInlining.cs",
            Dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    AssemblyName = "InlineableLib",
                    SourceResourceNames = new[] { "CrossModuleInlining/Dependencies/InlineableLib.cs" },
                    Crossgen = true,
                }
            },
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    [Fact]
    public void TransitiveReferences()
    {
        var expectations = new R2RExpectations();
        expectations.ExpectedManifestRefs.Add("InlineableLibTransitive");
        expectations.ExpectedManifestRefs.Add("ExternalLib");
        expectations.ExpectedInlinedMethods.Add(new ExpectedInlinedMethod("GetExternalValue"));
        expectations.Crossgen2Options.Add("--opt-cross-module:InlineableLibTransitive");

        var testCase = new R2RTestCase
        {
            Name = "TransitiveReferences",
            MainSourceResourceName = "CrossModuleInlining/TransitiveReferences.cs",
            Dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    AssemblyName = "ExternalLib",
                    SourceResourceNames = new[] { "CrossModuleInlining/Dependencies/ExternalLib.cs" },
                    Crossgen = false,
                },
                new DependencyInfo
                {
                    AssemblyName = "InlineableLibTransitive",
                    SourceResourceNames = new[] { "CrossModuleInlining/Dependencies/InlineableLibTransitive.cs" },
                    Crossgen = true,
                    AdditionalReferences = { "ExternalLib" },
                }
            },
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    [Fact]
    public void AsyncCrossModuleInlining()
    {
        var expectations = new R2RExpectations();
        expectations.ExpectedManifestRefs.Add("AsyncInlineableLib");
        expectations.ExpectedInlinedMethods.Add(new ExpectedInlinedMethod("GetValueAsync"));
        expectations.Crossgen2Options.Add("--opt-cross-module:AsyncInlineableLib");

        var testCase = new R2RTestCase
        {
            Name = "AsyncCrossModuleInlining",
            MainSourceResourceName = "CrossModuleInlining/AsyncMethods.cs",
            Dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    AssemblyName = "AsyncInlineableLib",
                    SourceResourceNames = new[] { "CrossModuleInlining/Dependencies/AsyncInlineableLib.cs" },
                    Crossgen = true,
                }
            },
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    [Fact]
    public void CompositeBasic()
    {
        var expectations = new R2RExpectations
        {
            CompositeMode = true,
        };
        expectations.ExpectedManifestRefs.Add("CompositeLib");

        var testCase = new R2RTestCase
        {
            Name = "CompositeBasic",
            MainSourceResourceName = "CrossModuleInlining/CompositeBasic.cs",
            Dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    AssemblyName = "CompositeLib",
                    SourceResourceNames = new[] { "CrossModuleInlining/Dependencies/CompositeLib.cs" },
                    Crossgen = true,
                }
            },
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    /// <summary>
    /// PR #124203: Async methods produce [ASYNC] variant entries with resumption stubs.
    /// PR #121456: Resumption stubs are emitted as ResumptionStubEntryPoint fixups.
    /// PR #123643: Methods with GC refs across awaits produce ContinuationLayout fixups.
    /// </summary>
    [Fact]
    public void RuntimeAsyncMethodEmission()
    {
        string attrSource = R2RTestCaseCompiler.ReadEmbeddedSource(
            "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs");
        string mainSource = R2RTestCaseCompiler.ReadEmbeddedSource(
            "RuntimeAsync/BasicAsyncEmission.cs");

        var expectations = new R2RExpectations();
        expectations.Features.Add(RuntimeAsyncFeature);
        expectations.ExpectedAsyncVariantMethods.Add("SimpleAsyncMethod");
        expectations.ExpectedAsyncVariantMethods.Add("AsyncVoidReturn");
        expectations.ExpectedAsyncVariantMethods.Add("ValueTaskMethod");

        var testCase = new R2RTestCase
        {
            Name = "RuntimeAsyncMethodEmission",
            MainSourceResourceName = "RuntimeAsync/BasicAsyncEmission.cs",
            MainExtraSourceResourceNames = new[] { "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs" },
            Dependencies = new List<DependencyInfo>(),
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    /// <summary>
    /// PR #123643: Async methods capturing GC refs across await points
    /// produce ContinuationLayout fixups encoding the GC ref map.
    /// PR #124203: Resumption stubs for methods with suspension points.
    /// </summary>
    [Fact]
    public void RuntimeAsyncContinuationLayout()
    {
        string attrSource = R2RTestCaseCompiler.ReadEmbeddedSource(
            "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs");
        string mainSource = R2RTestCaseCompiler.ReadEmbeddedSource(
            "RuntimeAsync/AsyncWithContinuation.cs");

        var expectations = new R2RExpectations
        {
            ExpectContinuationLayout = true,
            ExpectResumptionStubFixup = true,
        };
        expectations.Features.Add(RuntimeAsyncFeature);
        expectations.ExpectedAsyncVariantMethods.Add("CaptureObjectAcrossAwait");
        expectations.ExpectedAsyncVariantMethods.Add("CaptureMultipleRefsAcrossAwait");

        var testCase = new R2RTestCase
        {
            Name = "RuntimeAsyncContinuationLayout",
            MainSourceResourceName = "RuntimeAsync/AsyncWithContinuation.cs",
            MainExtraSourceResourceNames = new[] { "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs" },
            Dependencies = new List<DependencyInfo>(),
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    /// <summary>
    /// PR #125420: Devirtualization of async methods through
    /// AsyncAwareVirtualMethodResolutionAlgorithm.
    /// </summary>
    [Fact]
    public void RuntimeAsyncDevirtualize()
    {
        var expectations = new R2RExpectations();
        expectations.Features.Add(RuntimeAsyncFeature);
        expectations.ExpectedAsyncVariantMethods.Add("GetValueAsync");

        var testCase = new R2RTestCase
        {
            Name = "RuntimeAsyncDevirtualize",
            MainSourceResourceName = "RuntimeAsync/AsyncDevirtualize.cs",
            MainExtraSourceResourceNames = new[] { "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs" },
            Dependencies = new List<DependencyInfo>(),
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    /// <summary>
    /// PR #124203: Async methods without yield points may omit resumption stubs.
    /// Validates that no-yield async methods still produce [ASYNC] variants.
    /// </summary>
    [Fact]
    public void RuntimeAsyncNoYield()
    {
        var expectations = new R2RExpectations();
        expectations.Features.Add(RuntimeAsyncFeature);
        expectations.ExpectedAsyncVariantMethods.Add("AsyncButNoAwait");
        expectations.ExpectedAsyncVariantMethods.Add("AsyncWithConditionalAwait");

        var testCase = new R2RTestCase
        {
            Name = "RuntimeAsyncNoYield",
            MainSourceResourceName = "RuntimeAsync/AsyncNoYield.cs",
            MainExtraSourceResourceNames = new[] { "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs" },
            Dependencies = new List<DependencyInfo>(),
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }

    /// <summary>
    /// PR #121679: MutableModule async references + cross-module inlining
    /// of runtime-async methods with cross-module dependency.
    /// </summary>
    [Fact]
    public void RuntimeAsyncCrossModule()
    {
        var expectations = new R2RExpectations();
        expectations.Features.Add(RuntimeAsyncFeature);
        expectations.ExpectedManifestRefs.Add("AsyncDepLib");
        expectations.ExpectedAsyncVariantMethods.Add("CallCrossModuleAsync");
        expectations.Crossgen2Options.Add("--opt-cross-module:AsyncDepLib");

        var testCase = new R2RTestCase
        {
            Name = "RuntimeAsyncCrossModule",
            MainSourceResourceName = "RuntimeAsync/AsyncCrossModule.cs",
            MainExtraSourceResourceNames = new[] { "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs" },
            Dependencies = new List<DependencyInfo>
            {
                new DependencyInfo
                {
                    AssemblyName = "AsyncDepLib",
                    SourceResourceNames = new[]
                    {
                        "RuntimeAsync/Dependencies/AsyncDepLib.cs",
                        "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"
                    },
                    Crossgen = true,
                    Features = { RuntimeAsyncFeature },
                }
            },
            Expectations = expectations,
        };

        new R2RTestRunner().Run(testCase);
    }
}
