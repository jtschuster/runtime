// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias legacy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using ILCompiler.ReadyToRun.Tests.TestCasesRunner;
using Xunit;
using Xunit.Abstractions;

using LegacyR2R = legacy::ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;

namespace ILCompiler.ReadyToRun.Tests.TestCases;

/// <summary>
/// Runs the Structural↔legacy R2R parity check against every R2R-compiled assembly
/// in the built runtime pack. Provides broad oracle-based coverage of the Structural
/// reader on real-world images (System.Private.CoreLib and the framework BCL).
/// </summary>
public class RuntimePackParityTests
{
    private readonly ITestOutputHelper _output;

    public RuntimePackParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(RuntimePackR2RImages))]
    public void Parity(string imagePath)
    {
        Assert.True(File.Exists(imagePath), $"Image not found: {imagePath}");
        _output.WriteLine($"Parity check: {imagePath}");
        var paths = new TestPaths(_output);
        StructuralLegacyParity.AssertParity(imagePath, paths.RuntimePackDir, paths.RuntimePackNativeDir);
    }

    public static IEnumerable<object[]> RuntimePackR2RImages()
    {
        var output = new NullTestOutput();
        TestPaths paths;
        try
        {
            paths = new TestPaths(output);
        }
        catch
        {
            yield break;
        }

        foreach (string dll in EnumerateR2RDlls(paths.RuntimePackDir))
            yield return new object[] { dll };
        foreach (string dll in EnumerateR2RDlls(paths.RuntimePackNativeDir))
            yield return new object[] { dll };
        if (paths.RuntimePackR2RDir is string r2rDir)
        {
            foreach (string dll in EnumerateR2RDlls(r2rDir))
                yield return new object[] { dll };
        }
    }

    private static IEnumerable<string> EnumerateR2RDlls(string dir)
    {
        if (!Directory.Exists(dir))
            yield break;

        foreach (string path in Directory.EnumerateFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (IsReadyToRunImage(path))
                yield return path;
        }
    }

    private static bool IsReadyToRunImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return LegacyR2R.IsReadyToRunImage(pe);
        }
        catch
        {
            return false;
        }
    }

    private sealed class NullTestOutput : ITestOutputHelper
    {
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }
}
