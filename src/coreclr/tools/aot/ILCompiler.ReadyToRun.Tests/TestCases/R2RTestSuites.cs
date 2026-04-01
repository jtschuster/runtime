// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.ReadyToRun.Tests.TestCasesRunner;
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
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "BasicCrossModuleInlining",
            MainSourceResourceName = "CrossModuleInlining/BasicInlining.cs",
            Crossgen2Options = { Crossgen2Option.CrossModuleOptimization("InlineableLib") },
            Dependencies = new List<CompiledAssembly>
            {
                new()
                {
                    AssemblyName = "InlineableLib",
                    SourceResourceNames = ["CrossModuleInlining/Dependencies/InlineableLib.cs"],
                    IsCrossgenInput = true,
                }
            },
            Validate = reader =>
            {
                R2RAssert.HasManifestRef(reader, "InlineableLib");
                R2RAssert.HasInlinedMethod(reader, "GetValue");
                R2RAssert.HasInlinedMethod(reader, "GetString");
            },
        });
    }

    [Fact]
    public void TransitiveReferences()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "TransitiveReferences",
            MainSourceResourceName = "CrossModuleInlining/TransitiveReferences.cs",
            Crossgen2Options = { Crossgen2Option.CrossModuleOptimization("InlineableLibTransitive") },
            Dependencies = new List<CompiledAssembly>
            {
                new()
                {
                    AssemblyName = "ExternalLib",
                    SourceResourceNames = ["CrossModuleInlining/Dependencies/ExternalLib.cs"],
                    IsCrossgenInput = false,
                },
                new()
                {
                    AssemblyName = "InlineableLibTransitive",
                    SourceResourceNames = ["CrossModuleInlining/Dependencies/InlineableLibTransitive.cs"],
                    IsCrossgenInput = true,
                }
            },
            Validate = reader =>
            {
                R2RAssert.HasManifestRef(reader, "InlineableLibTransitive");
                R2RAssert.HasManifestRef(reader, "ExternalLib");
                R2RAssert.HasInlinedMethod(reader, "GetExternalValue");
            },
        });
    }

    [Fact]
    public void AsyncCrossModuleInlining()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "AsyncCrossModuleInlining",
            MainSourceResourceName = "CrossModuleInlining/AsyncMethods.cs",
            Crossgen2Options = { Crossgen2Option.CrossModuleOptimization("AsyncInlineableLib") },
            Dependencies = new List<CompiledAssembly>
            {
                new()
                {
                    AssemblyName = "AsyncInlineableLib",
                    SourceResourceNames = ["CrossModuleInlining/Dependencies/AsyncInlineableLib.cs"],
                    IsCrossgenInput = true,
                }
            },
            Validate = reader =>
            {
                R2RAssert.HasManifestRef(reader, "AsyncInlineableLib");
                R2RAssert.HasInlinedMethod(reader, "GetValueAsync");
            },
        });
    }

    [Fact]
    public void CompositeBasic()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "CompositeBasic",
            MainSourceResourceName = "CrossModuleInlining/CompositeBasic.cs",
            CompositeMode = true,
            Dependencies = new List<CompiledAssembly>
            {
                new()
                {
                    AssemblyName = "CompositeLib",
                    SourceResourceNames = ["CrossModuleInlining/Dependencies/CompositeLib.cs"],
                    IsCrossgenInput = true,
                }
            },
            Validate = reader =>
            {
                R2RAssert.HasManifestRef(reader, "CompositeLib");
            },
        });
    }

    /// <summary>
    /// PR #124203: Async methods produce [ASYNC] variant entries with resumption stubs.
    /// PR #121456: Resumption stubs are emitted as ResumptionStubEntryPoint fixups.
    /// PR #123643: Methods with GC refs across awaits produce ContinuationLayout fixups.
    /// </summary>
    [Fact]
    public void RuntimeAsyncMethodEmission()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "RuntimeAsyncMethodEmission",
            MainSourceResourceName = "RuntimeAsync/BasicAsyncEmission.cs",
            MainExtraSourceResourceNames = ["RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"],
            Features = { RuntimeAsyncFeature },
            Dependencies = new List<CompiledAssembly>(),
            Validate = reader =>
            {
                R2RAssert.HasAsyncVariant(reader, "SimpleAsyncMethod");
                R2RAssert.HasAsyncVariant(reader, "AsyncVoidReturn");
                R2RAssert.HasAsyncVariant(reader, "ValueTaskMethod");
            },
        });
    }

    /// <summary>
    /// PR #123643: Async methods capturing GC refs across await points
    /// produce ContinuationLayout fixups encoding the GC ref map.
    /// PR #124203: Resumption stubs for methods with suspension points.
    /// </summary>
    [Fact]
    public void RuntimeAsyncContinuationLayout()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "RuntimeAsyncContinuationLayout",
            MainSourceResourceName = "RuntimeAsync/AsyncWithContinuation.cs",
            MainExtraSourceResourceNames = ["RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"],
            Features = { RuntimeAsyncFeature },
            Dependencies = new List<CompiledAssembly>(),
            Validate = reader =>
            {
                R2RAssert.HasAsyncVariant(reader, "CaptureObjectAcrossAwait");
                R2RAssert.HasAsyncVariant(reader, "CaptureMultipleRefsAcrossAwait");
                R2RAssert.HasContinuationLayout(reader);
                R2RAssert.HasResumptionStubFixup(reader);
            },
        });
    }

    /// <summary>
    /// PR #125420: Devirtualization of async methods through
    /// AsyncAwareVirtualMethodResolutionAlgorithm.
    /// </summary>
    [Fact]
    public void RuntimeAsyncDevirtualize()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "RuntimeAsyncDevirtualize",
            MainSourceResourceName = "RuntimeAsync/AsyncDevirtualize.cs",
            MainExtraSourceResourceNames = ["RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"],
            Features = { RuntimeAsyncFeature },
            Dependencies = new List<CompiledAssembly>(),
            Validate = reader =>
            {
                R2RAssert.HasAsyncVariant(reader, "GetValueAsync");
            },
        });
    }

    /// <summary>
    /// PR #124203: Async methods without yield points may omit resumption stubs.
    /// Validates that no-yield async methods still produce [ASYNC] variants.
    /// </summary>
    [Fact]
    public void RuntimeAsyncNoYield()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "RuntimeAsyncNoYield",
            MainSourceResourceName = "RuntimeAsync/AsyncNoYield.cs",
            MainExtraSourceResourceNames = ["RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"],
            Features = { RuntimeAsyncFeature },
            Dependencies = new List<CompiledAssembly>(),
            Validate = reader =>
            {
                R2RAssert.HasAsyncVariant(reader, "AsyncButNoAwait");
                R2RAssert.HasAsyncVariant(reader, "AsyncWithConditionalAwait");
            },
        });
    }

    /// <summary>
    /// PR #121679: MutableModule async references + cross-module inlining
    /// of runtime-async methods with cross-module dependency.
    /// </summary>
    [Fact]
    public void RuntimeAsyncCrossModule()
    {
        new R2RTestRunner().Run(new R2RTestCase
        {
            Name = "RuntimeAsyncCrossModule",
            MainSourceResourceName = "RuntimeAsync/AsyncCrossModule.cs",
            MainExtraSourceResourceNames = ["RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"],
            Features = { RuntimeAsyncFeature },
            Crossgen2Options = { Crossgen2Option.CrossModuleOptimization("AsyncDepLib") },
            Dependencies = new List<CompiledAssembly>
            {
                new()
                {
                    AssemblyName = "AsyncDepLib",
                    SourceResourceNames =
                    [
                        "RuntimeAsync/Dependencies/AsyncDepLib.cs",
                        "RuntimeAsync/RuntimeAsyncMethodGenerationAttribute.cs"
                    ],
                    IsCrossgenInput = true,
                    Features = { RuntimeAsyncFeature },
                }
            },
            Validate = reader =>
            {
                R2RAssert.HasManifestRef(reader, "AsyncDepLib");
                R2RAssert.HasAsyncVariant(reader, "CallCrossModuleAsync");
            },
        });
    }
}
