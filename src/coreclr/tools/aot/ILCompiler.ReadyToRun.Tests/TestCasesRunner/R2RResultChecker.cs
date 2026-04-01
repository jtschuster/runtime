// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Parsed expectations from a test case's assembly-level and method-level attributes.
/// </summary>
internal sealed class R2RExpectations
{
    public List<string> ExpectedManifestRefs { get; } = new();
    public List<ExpectedInlinedMethod> ExpectedInlinedMethods { get; } = new();
    public bool CompositeMode { get; set; }
    public List<string> Crossgen2Options { get; } = new();
    /// <summary>
    /// Roslyn feature flags for the main assembly (e.g. runtime-async=on).
    /// </summary>
    public List<KeyValuePair<string, string>> Features { get; } = new();
    /// <summary>
    /// Method names expected to have [ASYNC] variant entries in the R2R image.
    /// </summary>
    public List<string> ExpectedAsyncVariantMethods { get; } = new();
    /// <summary>
    /// Method names expected to have [RESUME] (resumption stub) entries.
    /// </summary>
    public List<string> ExpectedResumptionStubs { get; } = new();
    /// <summary>
    /// If true, expect at least one ContinuationLayout fixup in the image.
    /// </summary>
    public bool ExpectContinuationLayout { get; set; }
    /// <summary>
    /// If true, expect at least one ResumptionStubEntryPoint fixup in the image.
    /// </summary>
    public bool ExpectResumptionStubFixup { get; set; }
    /// <summary>
    /// Fixup kinds that must be present somewhere in the image.
    /// </summary>
    public List<ReadyToRunFixupKind> ExpectedFixupKinds { get; } = new();
}

internal sealed record ExpectedInlinedMethod(string MethodName);

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
        CheckAsyncVariantMethods(reader, expectations, r2rImagePath);
        CheckResumptionStubs(reader, expectations, r2rImagePath);
        CheckFixupKinds(reader, expectations, r2rImagePath);
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

        var checkIlBodySignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formattingOptions = new SignatureFormattingOptions();
        var allFixupSummary = new List<string>();

        void CollectFixups(ReadyToRunMethod method)
        {
            foreach (var cell in method.Fixups)
            {
                if (cell.Signature is null)
                    continue;

                string sigText = cell.Signature.ToString(formattingOptions);
                allFixupSummary.Add($"[{cell.Signature.FixupKind}] {sigText}");

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
                $"All fixups: [{string.Join("; ", allFixupSummary)}]");
        }
    }

    private static List<ReadyToRunMethod> GetAllMethods(ReadyToRunReader reader)
    {
        var methods = new List<ReadyToRunMethod>();
        foreach (var assembly in reader.ReadyToRunAssemblies)
            methods.AddRange(assembly.Methods);
        foreach (var instanceMethod in reader.InstanceMethods)
            methods.Add(instanceMethod.Method);

        return methods;
    }

    private static void CheckAsyncVariantMethods(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedAsyncVariantMethods.Count == 0)
            return;

        var allMethods = GetAllMethods(reader);
        var asyncMethods = allMethods
            .Where(m => m.SignatureString.Contains("[ASYNC]", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SignatureString)
            .ToList();

        foreach (string expected in expectations.ExpectedAsyncVariantMethods)
        {
            bool found = asyncMethods.Any(sig =>
                sig.Contains(expected, StringComparison.OrdinalIgnoreCase));

            Assert.True(found,
                $"Expected [ASYNC] variant for '{expected}' not found in '{Path.GetFileName(imagePath)}'. " +
                $"Async methods: [{string.Join(", ", asyncMethods)}]. " +
                $"All methods: [{string.Join(", ", allMethods.Select(m => m.SignatureString).Take(30))}]");
        }
    }

    private static void CheckResumptionStubs(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedResumptionStubs.Count == 0 && !expectations.ExpectResumptionStubFixup)
            return;

        var allMethods = GetAllMethods(reader);
        var resumeMethods = allMethods
            .Where(m => m.SignatureString.Contains("[RESUME]", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SignatureString)
            .ToList();

        foreach (string expected in expectations.ExpectedResumptionStubs)
        {
            bool found = resumeMethods.Any(sig =>
                sig.Contains(expected, StringComparison.OrdinalIgnoreCase));

            Assert.True(found,
                $"Expected [RESUME] stub for '{expected}' not found in '{Path.GetFileName(imagePath)}'. " +
                $"Resume methods: [{string.Join(", ", resumeMethods)}]. " +
                $"All methods: [{string.Join(", ", allMethods.Select(m => m.SignatureString).Take(30))}]");
        }

        if (expectations.ExpectResumptionStubFixup)
        {
            var formattingOptions = new SignatureFormattingOptions();
            bool hasResumptionFixup = allMethods.Any(m =>
                m.Fixups.Any(c =>
                    c.Signature?.FixupKind == ReadyToRunFixupKind.ResumptionStubEntryPoint));

            Assert.True(hasResumptionFixup,
                $"Expected ResumptionStubEntryPoint fixup not found in '{Path.GetFileName(imagePath)}'.");
        }
    }

    private static void CheckFixupKinds(ReadyToRunReader reader, R2RExpectations expectations, string imagePath)
    {
        if (expectations.ExpectedFixupKinds.Count == 0 && !expectations.ExpectContinuationLayout)
            return;

        var allMethods = GetAllMethods(reader);
        var presentKinds = new HashSet<ReadyToRunFixupKind>();
        foreach (var method in allMethods)
        {
            if (method.Fixups is null)
                continue;
            foreach (var cell in method.Fixups)
            {
                if (cell.Signature is not null)
                    presentKinds.Add(cell.Signature.FixupKind);
            }
        }

        if (expectations.ExpectContinuationLayout)
        {
            Assert.True(presentKinds.Contains(ReadyToRunFixupKind.ContinuationLayout),
                $"Expected ContinuationLayout fixup not found in '{Path.GetFileName(imagePath)}'. " +
                $"Present fixup kinds: [{string.Join(", ", presentKinds)}]");
        }

        foreach (var expectedKind in expectations.ExpectedFixupKinds)
        {
            Assert.True(presentKinds.Contains(expectedKind),
                $"Expected fixup kind '{expectedKind}' not found in '{Path.GetFileName(imagePath)}'. " +
                $"Present fixup kinds: [{string.Join(", ", presentKinds)}]");
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
