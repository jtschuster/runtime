// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using ILCompiler.Reflection.ReadyToRun;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Describes a test case: a main source file with its dependencies and compilation options.
/// Validation is done via the <see cref="Validate"/> callback which receives the <see cref="ReadyToRunReader"/>.
/// </summary>
internal sealed class R2RTestCase
{
    public required string Name { get; init; }
    public required string MainSourceResourceName { get; init; }
    /// <summary>
    /// Additional source files to compile with the main assembly (e.g. shared attribute files).
    /// </summary>
    public string[]? MainExtraSourceResourceNames { get; init; }
    public required List<CompiledAssembly> Dependencies { get; init; }

    // Compilation config
    public bool CompositeMode { get; init; }
    public List<Crossgen2Option> Crossgen2Options { get; init; } = new();
    /// <summary>
    /// Roslyn feature flags for the main assembly (e.g. runtime-async=on).
    /// </summary>
    public List<KeyValuePair<string, string>> Features { get; init; } = new();

    /// <summary>
    /// Callback that receives the <see cref="ReadyToRunReader"/> for the main R2R image.
    /// Use <see cref="R2RAssert"/> helpers or raw xUnit assertions to validate the output.
    /// </summary>
    public required Action<ReadyToRunReader> Validate { get; init; }
}

/// <summary>
/// Describes an assembly compiled as part of a test case.
/// Dependencies are compiled in listed order — each assembly can reference all previously compiled assemblies.
/// </summary>
internal sealed class CompiledAssembly
{
    public required string AssemblyName { get; init; }
    public required string[] SourceResourceNames { get; init; }
    /// <summary>
    /// If true, this assembly is passed as an input to crossgen2.
    /// If false, it is only used as a reference (--ref) during crossgen2 compilation.
    /// </summary>
    public bool IsCrossgenInput { get; init; }
    /// <summary>
    /// Roslyn feature flags for this dependency (e.g. runtime-async=on).
    /// </summary>
    public List<KeyValuePair<string, string>> Features { get; init; } = new();
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

            // Step 1: Compile all dependencies with Roslyn (in order, leaf to root)
            var compiler = new R2RTestCaseCompiler(ilDir);
            var compiledDeps = new List<(CompiledAssembly Dep, string IlPath)>();

            foreach (var dep in testCase.Dependencies)
            {
                var sources = dep.SourceResourceNames
                    .Select(R2RTestCaseCompiler.ReadEmbeddedSource)
                    .ToList();

                // Each dependency can reference all previously compiled assemblies
                var refs = compiledDeps.Select(d => d.IlPath).ToList();

                string ilPath = compiler.CompileAssembly(dep.AssemblyName, sources, refs,
                    features: dep.Features.Count > 0 ? dep.Features : null);
                compiledDeps.Add((dep, ilPath));
            }

            // Step 2: Compile main assembly with Roslyn
            var mainSources = new List<string>
            {
                R2RTestCaseCompiler.ReadEmbeddedSource(testCase.MainSourceResourceName)
            };
            if (testCase.MainExtraSourceResourceNames is not null)
            {
                foreach (string extra in testCase.MainExtraSourceResourceNames)
                    mainSources.Add(R2RTestCaseCompiler.ReadEmbeddedSource(extra));
            }

            var mainRefs = compiledDeps.Select(d => d.IlPath).ToList();
            string mainIlPath = compiler.CompileAssembly(testCase.Name, mainSources, mainRefs,
                outputKind: Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                features: testCase.Features.Count > 0 ? testCase.Features : null);

            // Step 3: Crossgen2 dependencies
            var driver = new R2RDriver();
            var allRefPaths = BuildReferencePaths(ilDir);

            foreach (var (dep, ilPath) in compiledDeps)
            {
                if (!dep.IsCrossgenInput)
                    continue;

                string r2rPath = Path.Combine(r2rDir, Path.GetFileName(ilPath));
                var result = driver.Compile(new R2RCompilationOptions
                {
                    InputPath = ilPath,
                    OutputPath = r2rPath,
                    ReferencePaths = allRefPaths,
                });

                Assert.True(result.Success,
                    $"crossgen2 failed for dependency '{dep.AssemblyName}':\n{result.StandardError}\n{result.StandardOutput}");
            }

            // Step 4: Crossgen2 main assembly
            string mainR2RPath = Path.Combine(r2rDir, Path.GetFileName(mainIlPath));

            if (testCase.CompositeMode)
            {
                RunCompositeCompilation(testCase, driver, mainIlPath, mainR2RPath, allRefPaths, compiledDeps);
            }
            else
            {
                RunSingleCompilation(testCase, driver, mainIlPath, mainR2RPath, allRefPaths);
            }

            // Step 5: Validate R2R output
            Assert.True(File.Exists(mainR2RPath), $"R2R image not found: {mainR2RPath}");

            var reader = new ReadyToRunReader(new SimpleAssemblyResolver(), mainR2RPath);
            testCase.Validate(reader);
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
            ExtraArgs = testCase.Crossgen2Options.SelectMany(o => o.ToArgs()).ToList(),
        };

        var result = driver.Compile(options);
        Assert.True(result.Success,
            $"crossgen2 failed for main assembly '{testCase.Name}':\n{result.StandardError}\n{result.StandardOutput}");
    }

    private static void RunCompositeCompilation(
        R2RTestCase testCase,
        R2RDriver driver,
        string mainIlPath,
        string mainR2RPath,
        List<string> allRefPaths,
        List<(CompiledAssembly Dep, string IlPath)> compiledDeps)
    {
        var compositeInputs = new List<string> { mainIlPath };
        foreach (var (dep, ilPath) in compiledDeps)
        {
            if (dep.IsCrossgenInput)
                compositeInputs.Add(ilPath);
        }

        var options = new R2RCompilationOptions
        {
            InputPath = mainIlPath,
            OutputPath = mainR2RPath,
            ReferencePaths = allRefPaths,
            Composite = true,
            CompositeInputPaths = compositeInputs,
            ExtraArgs = testCase.Crossgen2Options.SelectMany(o => o.ToArgs()).ToList(),
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
