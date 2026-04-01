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
    public void AsyncMethodThunks()
    {
        var expectations = new R2RExpectations
        {
            RuntimeAsync = true,
        };
        expectations.ExpectedManifestRefs.Add("AsyncInlineableLib");
        expectations.ExpectedAsyncMethods.Add(new ExpectedAsyncMethod("TestAsyncInline", 3));
        expectations.Crossgen2Options.Add("--opt-cross-module:AsyncInlineableLib");

        var testCase = new R2RTestCase
        {
            Name = "AsyncMethodThunks",
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
}
