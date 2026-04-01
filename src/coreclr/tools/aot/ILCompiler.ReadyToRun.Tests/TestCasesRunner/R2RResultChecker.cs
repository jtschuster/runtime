// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILCompiler.Reflection.ReadyToRun;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Parsed expectations from a test case's assembly-level and method-level attributes.
/// </summary>
internal sealed class R2RExpectations
{
    public List<string> ExpectedManifestRefs { get; } = new();
    public List<ExpectedInlinedMethod> ExpectedInlinedMethods { get; } = new();
    public List<ExpectedAsyncMethod> ExpectedAsyncMethods { get; } = new();
    public bool CompositeMode { get; set; }
    public bool RuntimeAsync { get; set; }
    public List<string> Crossgen2Options { get; } = new();
}

internal sealed record ExpectedInlinedMethod(string MethodName);
internal sealed record ExpectedAsyncMethod(string MethodName, int ExpectedRuntimeFunctionCount);

/// <summary>
/// Validates R2R images against test expectations using ReadyToRunReader.
/// </summary>
internal sealed class R2RResultChecker
{
    /// <summary>
    /// Validates the main R2R image against expectations.
    /// </summary>
    public void Check(string r2rImagePath, R2RExpectations expectations)
    {
        Assert.True(File.Exists(r2rImagePath), $"R2R image not found: {r2rImagePath}");

        using var fileStream = File.OpenRead(r2rImagePath);
        using var peReader = new PEReader(fileStream);

        Assert.True(ReadyToRunReader.IsReadyToRunImage(peReader),
            $"'{Path.GetFileName(r2rImagePath)}' is not a valid R2R image");

        var reader = new ReadyToRunReader(new SimpleAssemblyResolver(), r2rImagePath);

        CheckManifestRefs(reader, expectations, r2rImagePath);
        CheckInlinedMethods(reader, expectations, r2rImagePath);
        CheckAsyncMethods(reader, expectations, r2rImagePath);
    }

    private static void CheckManifestRefs(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedManifestRefs.Count == 0)
            return;

        // Get all assembly references (both MSIL and manifest)
        var allRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read MSIL AssemblyRef table
        var globalMetadata = reader.GetGlobalMetadata();
        var mdReader = globalMetadata.MetadataReader;
        foreach (var handle in mdReader.AssemblyReferences)
        {
            var assemblyRef = mdReader.GetAssemblyReference(handle);
            string name = mdReader.GetString(assemblyRef.Name);
            allRefs.Add(name);
        }

        // Read manifest references (extra refs beyond MSIL table)
        foreach (var kvp in reader.ManifestReferenceAssemblies)
        {
            allRefs.Add(kvp.Key);
        }

        foreach (string expected in expectations.ExpectedManifestRefs)
        {
            Assert.True(allRefs.Contains(expected),
                $"Expected assembly reference '{expected}' not found in R2R image '{Path.GetFileName(imagePath)}'. " +
                $"Found: [{string.Join(", ", allRefs.OrderBy(s => s))}]");
        }
    }

    private static void CheckInlinedMethods(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedInlinedMethods.Count == 0)
            return;

        // Collect all fixup info: look for CHECK_IL_BODY fixup kind
        var checkIlBodySignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allMethodNames = new List<string>();
        var allFixups = new List<string>();
        var formattingOptions = new SignatureFormattingOptions();

        void CollectFixups(ReadyToRunMethod method)
        {
            allMethodNames.Add(method.SignatureString);
            foreach (var cell in method.Fixups)
            {
                if (cell.Signature is null)
                    continue;

                string sigText = cell.Signature.ToString(formattingOptions);
                allFixups.Add($"{method.SignatureString} -> [{cell.Signature.FixupKind}] {sigText}");

                if (cell.Signature.FixupKind is ReadyToRunFixupKind.Check_IL_Body or ReadyToRunFixupKind.Verify_IL_Body)
                {
                    checkIlBodySignatures.Add(sigText);
                }
            }
        }

        foreach (var assembly in reader.ReadyToRunAssemblies)
        {
            foreach (var method in assembly.Methods)
                CollectFixups(method);
        }

        foreach (var instanceMethod in reader.InstanceMethods)
            CollectFixups(instanceMethod.Method);

        foreach (var expected in expectations.ExpectedInlinedMethods)
        {
            bool found = checkIlBodySignatures.Any(f =>
                f.Contains(expected.MethodName, StringComparison.OrdinalIgnoreCase));

            Assert.True(found,
                $"Expected CHECK_IL_BODY fixup for '{expected.MethodName}' not found in '{Path.GetFileName(imagePath)}'. " +
                $"CHECK_IL_BODY fixups: [{string.Join(", ", checkIlBodySignatures)}]. " +
                $"All methods ({allMethodNames.Count}): [{string.Join(", ", allMethodNames.Take(30))}]. " +
                $"All fixups ({allFixups.Count}): [{string.Join("; ", allFixups.Take(30))}]");
        }
    }

    private static void CheckAsyncMethods(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedAsyncMethods.Count == 0)
            return;

        // Build method name -> RuntimeFunction count map from all sources
        var methodFunctionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in reader.ReadyToRunAssemblies)
        {
            foreach (var method in assembly.Methods)
            {
                methodFunctionCounts[method.SignatureString] = method.RuntimeFunctions.Count;
            }
        }

        foreach (var instanceMethod in reader.InstanceMethods)
        {
            methodFunctionCounts[instanceMethod.Method.SignatureString] = instanceMethod.Method.RuntimeFunctions.Count;
        }

        foreach (var expected in expectations.ExpectedAsyncMethods)
        {
            var match = methodFunctionCounts
                .FirstOrDefault(kvp => kvp.Key.Contains(expected.MethodName, StringComparison.OrdinalIgnoreCase));

            Assert.True(match.Key is not null,
                $"Expected async method '{expected.MethodName}' not found in R2R image '{Path.GetFileName(imagePath)}'. " +
                $"Found methods: [{string.Join(", ", methodFunctionCounts.Keys.Take(20))}...]");

            Assert.True(match.Value >= expected.ExpectedRuntimeFunctionCount,
                $"Async method '{expected.MethodName}' has {match.Value} RuntimeFunctions, " +
                $"expected >= {expected.ExpectedRuntimeFunctionCount} in '{Path.GetFileName(imagePath)}'");
        }
    }
}

/// <summary>
/// Simple assembly resolver that looks in the same directory as the input image.
/// </summary>
internal sealed class SimpleAssemblyResolver : IAssemblyResolver
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IAssemblyMetadata? FindAssembly(MetadataReader metadataReader, AssemblyReferenceHandle assemblyReferenceHandle, string parentFile)
    {
        var assemblyRef = metadataReader.GetAssemblyReference(assemblyReferenceHandle);
        string name = metadataReader.GetString(assemblyRef.Name);
        return FindAssembly(name, parentFile);
    }

    public IAssemblyMetadata? FindAssembly(string simpleName, string parentFile)
    {
        string? dir = Path.GetDirectoryName(parentFile);
        if (dir is null)
            return null;

        string candidate = Path.Combine(dir, simpleName + ".dll");
        if (!File.Exists(candidate))
        {
            // Try in runtime pack
            candidate = Path.Combine(TestPaths.RuntimePackDir, simpleName + ".dll");
        }

        if (!File.Exists(candidate))
            return null;

        return new SimpleAssemblyMetadata(candidate);
    }
}

/// <summary>
/// Simple assembly metadata wrapper.
/// </summary>
internal sealed class SimpleAssemblyMetadata : IAssemblyMetadata, IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _peReader;

    public SimpleAssemblyMetadata(string path)
    {
        _stream = File.OpenRead(path);
        _peReader = new PEReader(_stream);
    }

    public PEReader ImageReader => _peReader;

    public MetadataReader MetadataReader => _peReader.GetMetadataReader();

    public void Dispose()
    {
        _peReader.Dispose();
        _stream.Dispose();
    }
}
