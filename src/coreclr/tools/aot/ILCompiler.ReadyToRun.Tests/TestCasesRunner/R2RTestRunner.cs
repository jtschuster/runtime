// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILCompiler.ReadyToRun.Tests.Expectations;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Describes a test case: a main source file with its dependencies and expectations.
/// </summary>
internal sealed class R2RTestCase
{
    public required string Name { get; init; }
    public required string MainSourceResourceName { get; init; }
    public required List<DependencyInfo> Dependencies { get; init; }
    public required R2RExpectations Expectations { get; init; }
}

/// <summary>
/// Describes a dependency assembly for a test case.
/// </summary>
internal sealed class DependencyInfo
{
    public required string AssemblyName { get; init; }
    public required string[] SourceResourceNames { get; init; }
    public bool Crossgen { get; init; }
    public List<string> CrossgenOptions { get; init; } = new();
    public List<string> AdditionalReferences { get; init; } = new();
}

/// <summary>
/// Orchestrates the full R2R test pipeline: compile → crossgen2 → validate.
/// </summary>
internal sealed class R2RTestRunner
{
    /// <summary>
    /// Runs a test case end-to-end.
    /// </summary>
    public void Run(R2RTestCase testCase)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "R2RTests", testCase.Name, Guid.NewGuid().ToString("N")[..8]);
        string ilDir = Path.Combine(tempDir, "il");
        string r2rDir = Path.Combine(tempDir, "r2r");

        try
        {
            Directory.CreateDirectory(ilDir);
            Directory.CreateDirectory(r2rDir);

            // Step 1: Compile all dependencies with Roslyn
            var compiler = new R2RTestCaseCompiler(ilDir);
            var compiledDeps = new List<(DependencyInfo Dep, string IlPath)>();

            foreach (var dep in testCase.Dependencies)
            {
                var sources = dep.SourceResourceNames
                    .Select(R2RTestCaseCompiler.ReadEmbeddedSource)
                    .ToList();

                var refs = dep.AdditionalReferences
                    .Select(r => compiledDeps.First(d => d.Dep.AssemblyName == r).IlPath)
                    .ToList();

                string ilPath = compiler.CompileAssembly(dep.AssemblyName, sources, refs);
                compiledDeps.Add((dep, ilPath));
            }

            // Step 2: Compile main assembly with Roslyn
            string mainSource = R2RTestCaseCompiler.ReadEmbeddedSource(testCase.MainSourceResourceName);
            var mainRefs = compiledDeps.Select(d => d.IlPath).ToList();
            string mainIlPath = compiler.CompileAssembly(testCase.Name, new[] { mainSource }, mainRefs,
                outputKind: Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary);

            // Step 3: Crossgen2 dependencies
            var driver = new R2RDriver();
            var allRefPaths = BuildReferencePaths(ilDir);

            foreach (var (dep, ilPath) in compiledDeps)
            {
                if (!dep.Crossgen)
                    continue;

                string r2rPath = Path.Combine(r2rDir, Path.GetFileName(ilPath));
                var result = driver.Compile(new R2RCompilationOptions
                {
                    InputPath = ilPath,
                    OutputPath = r2rPath,
                    ReferencePaths = allRefPaths,
                    ExtraArgs = dep.CrossgenOptions,
                });

                Assert.True(result.Success,
                    $"crossgen2 failed for dependency '{dep.AssemblyName}':\n{result.StandardError}\n{result.StandardOutput}");
            }

            // Step 4: Crossgen2 main assembly
            string mainR2RPath = Path.Combine(r2rDir, Path.GetFileName(mainIlPath));

            if (testCase.Expectations.CompositeMode)
            {
                RunCompositeCompilation(testCase, driver, ilDir, r2rDir, mainIlPath, mainR2RPath, allRefPaths, compiledDeps);
            }
            else
            {
                RunSingleCompilation(testCase, driver, mainIlPath, mainR2RPath, allRefPaths);
            }

            // Step 5: Validate R2R output
            var checker = new R2RResultChecker();
            checker.Check(mainR2RPath, testCase.Expectations);
        }
        finally
        {
            // Keep temp directory for debugging if KEEP_R2R_TESTS env var is set
            if (Environment.GetEnvironmentVariable("KEEP_R2R_TESTS") is null)
            {
                try { Directory.Delete(tempDir, true); }
                catch { /* best effort */ }
            }
        }
    }

    private static void RunSingleCompilation(
        R2RTestCase testCase,
        R2RDriver driver,
        string mainIlPath,
        string mainR2RPath,
        List<string> allRefPaths)
    {
        var options = new R2RCompilationOptions
        {
            InputPath = mainIlPath,
            OutputPath = mainR2RPath,
            ReferencePaths = allRefPaths,
            ExtraArgs = testCase.Expectations.Crossgen2Options.ToList(),
        };

        var result = driver.Compile(options);
        Assert.True(result.Success,
            $"crossgen2 failed for main assembly '{testCase.Name}':\n{result.StandardError}\n{result.StandardOutput}");
    }

    private static void RunCompositeCompilation(
        R2RTestCase testCase,
        R2RDriver driver,
        string ilDir,
        string r2rDir,
        string mainIlPath,
        string mainR2RPath,
        List<string> allRefPaths,
        List<(DependencyInfo Dep, string IlPath)> compiledDeps)
    {
        var compositeInputs = new List<string> { mainIlPath };
        foreach (var (dep, ilPath) in compiledDeps)
        {
            if (dep.Crossgen)
                compositeInputs.Add(ilPath);
        }

        var options = new R2RCompilationOptions
        {
            InputPath = mainIlPath,
            OutputPath = mainR2RPath,
            ReferencePaths = allRefPaths,
            Composite = true,
            CompositeInputPaths = compositeInputs,
            ExtraArgs = testCase.Expectations.Crossgen2Options.ToList(),
        };

        var result = driver.Compile(options);
        Assert.True(result.Success,
            $"crossgen2 composite compilation failed for '{testCase.Name}':\n{result.StandardError}\n{result.StandardOutput}");
    }

    private static List<string> BuildReferencePaths(string ilDir)
    {
        var paths = new List<string>();

        // Add all compiled IL assemblies as references
        paths.Add(Path.Combine(ilDir, "*.dll"));

        // Add framework references (managed assemblies)
        paths.Add(Path.Combine(TestPaths.RuntimePackDir, "*.dll"));

        // System.Private.CoreLib is in the native directory, not lib
        string runtimePackDir = TestPaths.RuntimePackDir;
        string nativeDir = Path.GetFullPath(Path.Combine(runtimePackDir, "..", "..", "native"));
        if (Directory.Exists(nativeDir))
        {
            string spcl = Path.Combine(nativeDir, "System.Private.CoreLib.dll");
            if (File.Exists(spcl))
                paths.Add(spcl);
        }

        return paths;
    }
}
