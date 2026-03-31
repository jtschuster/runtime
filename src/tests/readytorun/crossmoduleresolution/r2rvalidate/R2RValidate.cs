// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;

/// <summary>
/// Validates R2R images for expected cross-module inlining artifacts.
/// Usage: R2RValidate --in &lt;r2r.dll&gt; --ref &lt;dir&gt;
///        [--expect-manifest-ref &lt;assemblyName&gt;]...
///        [--expect-inlined &lt;methodSubstring&gt;]...
///        [--expect-async-thunks &lt;methodSubstring&gt;]...
///        [--expect-no-inlining &lt;methodSubstring&gt;]...
/// </summary>
class R2RValidate
{
    static int Main(string[] args)
    {
        string inputFile = null;
        var refPaths = new List<string>();
        var expectedManifestRefs = new List<string>();
        var expectedInlined = new List<string>();
        var expectedAsyncThunks = new List<string>();
        var expectedNoInlining = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in":
                    inputFile = args[++i];
                    break;
                case "--ref":
                    refPaths.Add(args[++i]);
                    break;
                case "--expect-manifest-ref":
                    expectedManifestRefs.Add(args[++i]);
                    break;
                case "--expect-inlined":
                    expectedInlined.Add(args[++i]);
                    break;
                case "--expect-async-thunks":
                    expectedAsyncThunks.Add(args[++i]);
                    break;
                case "--expect-no-inlining":
                    expectedNoInlining.Add(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 1;
            }
        }

        if (inputFile is null)
        {
            Console.Error.WriteLine("Usage: R2RValidate --in <r2r.dll> --ref <dir> [options]");
            return 1;
        }

        try
        {
            return Validate(inputFile, refPaths, expectedManifestRefs, expectedInlined, expectedAsyncThunks, expectedNoInlining);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    static int Validate(
        string inputFile,
        List<string> refPaths,
        List<string> expectedManifestRefs,
        List<string> expectedInlined,
        List<string> expectedAsyncThunks,
        List<string> expectedNoInlining)
    {
        var resolver = new SimpleAssemblyResolver(refPaths);
        var reader = new ReadyToRunReader(resolver, inputFile);

        Console.WriteLine($"R2R Image: {inputFile}");
        Console.WriteLine($"  Composite: {reader.Composite}");

        // Collect all assembly references (MSIL + manifest metadata)
        var allAssemblyRefs = new Dictionary<string, int>();

        if (!reader.Composite)
        {
            var globalReader = reader.GetGlobalMetadata().MetadataReader;
            int msilRefCount = globalReader.GetTableRowCount(TableIndex.AssemblyRef);
            for (int i = 1; i <= msilRefCount; i++)
            {
                var asmRef = globalReader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(i));
                string name = globalReader.GetString(asmRef.Name);
                allAssemblyRefs[name] = i;
            }
        }
        foreach (var kvp in reader.ManifestReferenceAssemblies)
            allAssemblyRefs[kvp.Key] = kvp.Value;

        Console.WriteLine($"  Assembly references count: {allAssemblyRefs.Count}");
        foreach (var kvp in allAssemblyRefs.OrderBy(k => k.Value))
            Console.WriteLine($"    [{kvp.Value}] {kvp.Key}");

        var methods = reader.Methods.ToList();
        Console.WriteLine($"  R2R Methods count: {methods.Count}");

        bool allPassed = true;

        // 1. Validate assembly references (MSIL + manifest)
        foreach (string expected in expectedManifestRefs)
        {
            if (allAssemblyRefs.ContainsKey(expected))
            {
                Console.WriteLine($"  PASS: AssemblyRef '{expected}' found (index {allAssemblyRefs[expected]})");
            }
            else
            {
                Console.Error.WriteLine($"  FAIL: AssemblyRef '{expected}' NOT found. Available: [{string.Join(", ", allAssemblyRefs.Keys)}]");
                allPassed = false;
            }
        }

        // Build method lookup
        // (methods already populated above for diagnostics)

        // 2. Validate CHECK_IL_BODY fixups (proof of cross-module inlining)
        foreach (string pattern in expectedInlined)
        {
            var matching = methods.Where(m => MethodMatches(m, pattern)).ToList();
            if (matching.Count == 0)
            {
                Console.Error.WriteLine($"  FAIL: No R2R method matching '{pattern}' found");
                allPassed = false;
                continue;
            }

            foreach (var method in matching)
            {
                bool hasCheckILBody = method.Fixups != null &&
                    method.Fixups.Any(f => f.Signature?.FixupKind == ReadyToRunFixupKind.Check_IL_Body ||
                                           f.Signature?.FixupKind == ReadyToRunFixupKind.Verify_IL_Body);

                if (hasCheckILBody)
                {
                    Console.WriteLine($"  PASS: '{method.SignatureString}' has CHECK_IL_BODY fixup (cross-module inlining confirmed)");
                }
                else
                {
                    string fixupKinds = method.Fixups is null ? "none" :
                        string.Join(", ", method.Fixups.Select(f => f.Signature?.FixupKind.ToString() ?? "null"));
                    Console.Error.WriteLine($"  FAIL: '{method.SignatureString}' has NO CHECK_IL_BODY fixup. Fixups: [{fixupKinds}]");
                    allPassed = false;
                }
            }
        }

        // 3. Validate async thunks (3+ RuntimeFunctions per method)
        foreach (string pattern in expectedAsyncThunks)
        {
            var matching = methods.Where(m => MethodMatches(m, pattern)).ToList();
            if (matching.Count == 0)
            {
                Console.Error.WriteLine($"  FAIL: No R2R method matching '{pattern}' found for async thunk check");
                allPassed = false;
                continue;
            }

            foreach (var method in matching)
            {
                int rtfCount = method.RuntimeFunctions.Count;
                if (rtfCount >= 3)
                {
                    Console.WriteLine($"  PASS: '{method.SignatureString}' has {rtfCount} RuntimeFunctions (async thunk confirmed)");
                }
                else
                {
                    Console.Error.WriteLine($"  FAIL: '{method.SignatureString}' has only {rtfCount} RuntimeFunction(s), expected >= 3 for async thunk");
                    allPassed = false;
                }
            }
        }

        // 4. Validate methods that should NOT have cross-module inlining
        foreach (string pattern in expectedNoInlining)
        {
            var matching = methods.Where(m => MethodMatches(m, pattern)).ToList();
            if (matching.Count == 0)
            {
                // Method not in R2R at all — that's fine for this check
                Console.WriteLine($"  PASS: '{pattern}' not in R2R (no inlining, as expected)");
                continue;
            }

            foreach (var method in matching)
            {
                bool hasCheckILBody = method.Fixups != null &&
                    method.Fixups.Any(f => f.Signature?.FixupKind == ReadyToRunFixupKind.Check_IL_Body ||
                                           f.Signature?.FixupKind == ReadyToRunFixupKind.Verify_IL_Body);

                if (!hasCheckILBody)
                {
                    Console.WriteLine($"  PASS: '{method.SignatureString}' has no CHECK_IL_BODY fixup (no cross-module inlining, as expected)");
                }
                else
                {
                    Console.Error.WriteLine($"  FAIL: '{method.SignatureString}' unexpectedly has CHECK_IL_BODY fixup");
                    allPassed = false;
                }
            }
        }

        // Summary
        Console.WriteLine();
        if (allPassed)
        {
            Console.WriteLine($"R2R VALIDATION PASSED: {inputFile}");
            return 100;
        }
        else
        {
            Console.Error.WriteLine($"R2R VALIDATION FAILED: {inputFile}");
            return 1;
        }
    }

    static bool MethodMatches(ReadyToRunMethod method, string pattern)
    {
        string sig = method.SignatureString;
        if (sig is null)
            return false;
        // Skip [ASYNC] and [RESUME] sub-entries — only match primary method entries
        if (sig.StartsWith("[ASYNC]") || sig.StartsWith("[RESUME]"))
            return false;
        return sig.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Simple assembly resolver that probes reference directories.
/// </summary>
class SimpleAssemblyResolver : IAssemblyResolver
{
    private static readonly string[] s_probeExtensions = { ".ni.exe", ".ni.dll", ".exe", ".dll" };
    private readonly List<string> _refPaths;

    public SimpleAssemblyResolver(List<string> refPaths)
    {
        _refPaths = refPaths;
    }

    public IAssemblyMetadata FindAssembly(MetadataReader metadataReader, AssemblyReferenceHandle assemblyReferenceHandle, string parentFile)
    {
        string simpleName = metadataReader.GetString(metadataReader.GetAssemblyReference(assemblyReferenceHandle).Name);
        return FindAssembly(simpleName, parentFile);
    }

    public IAssemblyMetadata FindAssembly(string simpleName, string parentFile)
    {
        var allPaths = new List<string> { Path.GetDirectoryName(parentFile) };
        allPaths.AddRange(_refPaths);

        foreach (string refPath in allPaths)
        {
            foreach (string ext in s_probeExtensions)
            {
                string probeFile = Path.Combine(refPath, simpleName + ext);
                if (File.Exists(probeFile))
                {
                    try
                    {
                        return Open(probeFile);
                    }
                    catch (BadImageFormatException)
                    {
                    }
                }
            }
        }

        return null;
    }

    private static IAssemblyMetadata Open(string filename)
    {
        byte[] image = File.ReadAllBytes(filename);
        PEReader peReader = new(Unsafe.As<byte[], ImmutableArray<byte>>(ref image));
        if (!peReader.HasMetadata)
            throw new BadImageFormatException($"ECMA metadata not found in file '{filename}'");
        return new StandaloneAssemblyMetadata(peReader);
    }
}
