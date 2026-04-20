// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias legacy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using ILCompiler.ReadyToRun.Tests.TestCasesRunner;
using ILCompiler.Reflection.ReadyToRun;

using Xunit;
using Xunit.Abstractions;

using StructReader = ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;
using StructSectionType = Internal.Runtime.ReadyToRunSectionType;

using LegacyR2R = legacy::ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;
using LegacyCrossModule = legacy::ILCompiler.Reflection.ReadyToRun.CrossModuleInliningInfoSection;
using LegacySectionType = legacy::Internal.Runtime.ReadyToRunSectionType;
using LegacyIAsmResolver = legacy::ILCompiler.Reflection.ReadyToRun.IAssemblyResolver;
using LegacyIAsmMetadata = legacy::ILCompiler.Reflection.ReadyToRun.IAssemblyMetadata;
using LegacyStandaloneMetadata = legacy::ILCompiler.Reflection.ReadyToRun.StandaloneAssemblyMetadata;

namespace ILCompiler.ReadyToRun.Tests.TestCases;

/// <summary>
/// Regression tests for known bugs in the Structural R2R reader. Each test is expected
/// to FAIL today (against an unfixed reader) and PASS once the corresponding fix lands.
/// They sweep all R2R-compiled assemblies in the built runtime pack so the bug is caught
/// on whichever real-world image first triggers it.
///
/// See <c>session-state/.../files/structural-reader-audit.md</c> for the audit report.
/// </summary>
public class StructuralReaderBugTests
{
    private readonly ITestOutputHelper _output;

    public StructuralReaderBugTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> RuntimePackR2RImages()
    {
        TestPaths paths;
        try
        {
            paths = new TestPaths(new NullTestOutput());
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

    /// <summary>
    /// B1 — <c>MethodIsGenericMapTable.IsGeneric</c> reads bits LSB-first, but the
    /// crossgen2 emitter (<c>MethodIsGenericMapNode</c>) writes them MSB-first. Compare
    /// the reader's answer against the truth from <see cref="MetadataReader"/> for every
    /// MethodDef. Any mismatch is a bug.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimePackR2RImages))]
    public void MethodIsGenericMap_AgreesWith_MetadataReader(string imagePath)
    {
        Assert.True(File.Exists(imagePath), $"Image not found: {imagePath}");

        byte[] bytes = File.ReadAllBytes(imagePath);
        using var peReader = new PEReader(new MemoryStream(bytes));
        using var nativeReader = new NativeReader(new MemoryStream(bytes));
        using var struc = new StructReader(new PEImageReader(peReader, leaveOpen: true), nativeReader, imagePath);

        ReadyToRunSection mapSection = struc.GetSections()
            .FirstOrDefault(s => s.Type == StructSectionType.MethodIsGenericMap);
        if (mapSection.Type != StructSectionType.MethodIsGenericMap)
        {
            _output.WriteLine($"[skip] {Path.GetFileName(imagePath)}: no MethodIsGenericMap section");
            return;
        }

        if (!peReader.HasMetadata)
        {
            _output.WriteLine($"[skip] {Path.GetFileName(imagePath)}: image has no managed metadata");
            return;
        }

        MetadataReader mr = peReader.GetMetadataReader();
        MethodIsGenericMapTable table = struc.GetMethodIsGenericMapTable(mapSection);

        var mismatches = new List<string>();
        foreach (MethodDefinitionHandle handle in mr.MethodDefinitions)
        {
            int rid = MetadataTokens.GetRowNumber(handle);
            if (rid > table.Count)
                continue;

            MethodDefinition md = mr.GetMethodDefinition(handle);
            bool truth = md.GetGenericParameters().Count > 0;
            bool reported = table.IsGeneric(rid);
            if (truth != reported)
            {
                string name = mr.GetString(md.Name);
                mismatches.Add($"  rid={rid} '{name}' truth={truth} reported={reported}");
                if (mismatches.Count >= 10)
                    break;
            }
        }

        Assert.True(mismatches.Count == 0,
            $"MethodIsGenericMap mismatches in '{Path.GetFileName(imagePath)}' " +
            $"(showing up to 10):{Environment.NewLine}{string.Join(Environment.NewLine, mismatches)}");
    }

    /// <summary>
    /// M1 — <c>ExceptionInfoTable</c> includes the trailing <c>(MethodRva = ~0u, …)</c>
    /// sentinel as a real entry. Legacy correctly stops one short of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimePackR2RImages))]
    public void ExceptionInfoTable_DoesNotLeakSentinel(string imagePath)
    {
        Assert.True(File.Exists(imagePath), $"Image not found: {imagePath}");

        byte[] bytes = File.ReadAllBytes(imagePath);
        using var peReader = new PEReader(new MemoryStream(bytes));
        using var nativeReader = new NativeReader(new MemoryStream(bytes));
        using var struc = new StructReader(new PEImageReader(peReader, leaveOpen: true), nativeReader, imagePath);

        ReadyToRunSection ehSection = struc.GetSections()
            .FirstOrDefault(s => s.Type == StructSectionType.ExceptionInfo);
        if (ehSection.Type != StructSectionType.ExceptionInfo)
        {
            _output.WriteLine($"[skip] {Path.GetFileName(imagePath)}: no ExceptionInfo section");
            return;
        }

        ExceptionInfoTable table = struc.GetExceptionInfoTable(ehSection);
        Assert.NotEmpty(table.Entries);

        const uint SentinelMethodRva = unchecked((uint)~0);
        var sentinelEntries = table.Entries
            .Select((e, i) => (Index: i, Entry: e))
            .Where(t => (uint)t.Entry.MethodRva == SentinelMethodRva)
            .ToList();

        Assert.True(sentinelEntries.Count == 0,
            $"ExceptionInfoTable in '{Path.GetFileName(imagePath)}' contains " +
            $"{sentinelEntries.Count} sentinel entry/entries with MethodRva=~0u (e.g. index " +
            $"{(sentinelEntries.Count > 0 ? sentinelEntries[0].Index : -1)} of {table.Entries.Count}). " +
            $"The sentinel terminates the encoding and should not be exposed.");
    }

    /// <summary>
    /// B2 — In <c>CrossModuleInlineInfoTable</c>, when an inliner has the
    /// <c>InlinerRidHasModule</c> flag clear, its <c>ModuleIndex</c> must be inherited
    /// from the inlinee's module index. The Structural reader resets it to 0 instead.
    /// Compared against the legacy <c>CrossModuleInliningInfoSection</c> as the oracle.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimePackR2RImages))]
    public void CrossModuleInlineInfo_ModuleIndex_AgreesWith_Legacy(string imagePath)
    {
        AssertCrossModuleInlineInfoModuleIndexParity(imagePath, _output, requireSection: false);
    }

    /// <summary>
    /// B2 — guaranteed-coverage variant: builds a composite crossgen2 image with
    /// <c>--opt-cross-module</c> on every input and an intra-module inline so the buggy
    /// inheritance path in <c>CrossModuleInlineInfoTable</c> is actually exercised
    /// (the runtime-pack Theory above silently skips on every image in CI).
    /// </summary>
    [Fact]
    public void CrossModuleInlineInfo_ModuleIndex_AgreesWith_Legacy_CrossModuleImage()
    {
        // To trip B2 (CrossModuleInlineInfoTable.cs:80 hard-codes moduleIndex=0 instead of
        // inheriting inlineeModuleIndex), we need an entry whose inliner shares the same
        // module as a non-cross-module inlinee with non-zero ModuleIndex. That requires:
        //   1. Multi-module version bubble (READYTORUN_FLAG_MultiModuleVersionBubble)
        //   2. CrossModuleInlineInfo emitted with at least one entry
        //   3. An entry whose inlinee has ModuleIndex != 0 (so the inheritance code path
        //      in the reader actually sets a non-zero value vs. the bug's hard-coded 0).
        //   4. At least one inliner on that entry where InlinerRidHasModule is clear
        //      (i.e. inliner.Module == inlinee.Module, see InliningInfoNode.cs:343-352).
        //
        // Recipe (verified against InliningInfoNode + ManifestMetadataTableNode +
        // ReadyToRunCompilationModuleGroupBase):
        //   * Composite mode with >= 2 inputs. ManifestMetadataTableNode.cs:143-156
        //     assigns each composite input a non-zero slot (2, 3, ...); the early-return
        //     to slot 0 at lines 228-232 only fires for non-composite single-module
        //     compiles, which is what masked the bug previously.
        //   * --opt-cross-module on a Reference assembly OUTSIDE the bubble. This sets
        //     CrossModuleGenericCompilation=true (Program.cs:445) so CrossModuleCompileable
        //     can return true (ReadyToRunCompilationModuleGroupBase.cs:432-457), letting
        //     the InliningInfoNode filter (line 86) actually emit intra-bubble generic
        //     inlines into CrossModuleInlineInfo.
        //   * A generic intra-module inline (IntraModuleInline<T>.TestHelper -> Helper)
        //     so inlinee.Module == inliner.Module and isForeignInliner is false.
        var externalLib = new CompiledAssembly
        {
            AssemblyName = "ExternalLib",
            SourceResourceNames = ["CrossModuleInlining/Dependencies/ExternalLib.cs"],
        };
        var inlineableLib = new CompiledAssembly
        {
            AssemblyName = "InlineableLib",
            SourceResourceNames = ["CrossModuleInlining/Dependencies/InlineableLib.cs"],
        };
        var consumer = new CompiledAssembly
        {
            AssemblyName = "B2ReproIntraModule",
            SourceResourceNames = ["CrossModuleInlining/IntraModuleInline.cs"],
            References = [inlineableLib],
        };

        // Two composite inputs (in the version bubble) + one external --opt-cross-module
        // Reference (outside the bubble) to enable CrossModuleGenericCompilation.
        var cgInlineableLib = new CrossgenAssembly(inlineableLib);
        var cgConsumer = new CrossgenAssembly(consumer);
        var cgExternalLib = new CrossgenAssembly(externalLib)
        {
            Kind = Crossgen2InputKind.Reference,
            Options = [Crossgen2AssemblyOption.CrossModuleOptimization],
        };

        // Validate runs while R2RTestRunner still owns the output directory; capture the
        // compilation in a closure so its FilePath is available for the parity check.
        CrossgenCompilation compilation = null!;
        compilation = new CrossgenCompilation(consumer.AssemblyName, [cgInlineableLib, cgConsumer, cgExternalLib])
        {
            Options = [Crossgen2Option.Composite, Crossgen2Option.Optimize],
            Validate = _ => AssertCrossModuleInlineInfoModuleIndexParity(
                compilation.FilePath, _output, requireSection: true),
        };

        new R2RTestRunner(_output).Run(new R2RTestCase(
            nameof(CrossModuleInlineInfo_ModuleIndex_AgreesWith_Legacy_CrossModuleImage),
            [compilation]));
    }

    private static void AssertCrossModuleInlineInfoModuleIndexParity(
        string imagePath, ITestOutputHelper output, bool requireSection)
    {
        Assert.True(File.Exists(imagePath), $"Image not found: {imagePath}");

        byte[] bytes = File.ReadAllBytes(imagePath);
        using var peReader = new PEReader(new MemoryStream(bytes));
        using var nativeReader = new NativeReader(new MemoryStream(bytes));
        using var struc = new StructReader(new PEImageReader(peReader, leaveOpen: true), nativeReader, imagePath);

        ReadyToRunSection xmSection = struc.GetSections()
            .FirstOrDefault(s => s.Type == StructSectionType.CrossModuleInlineInfo);
        if (xmSection.Type != StructSectionType.CrossModuleInlineInfo)
        {
            Assert.False(requireSection,
                $"Expected CrossModuleInlineInfo section in '{Path.GetFileName(imagePath)}' but none was emitted.");
            output.WriteLine($"[skip] {Path.GetFileName(imagePath)}: no CrossModuleInlineInfo section");
            return;
        }

        var paths = new TestPaths(output);
        var resolver = new ParityAssemblyResolver(new[]
        {
            Path.GetDirectoryName(imagePath)!,
            paths.RuntimePackDir,
            paths.RuntimePackNativeDir,
        });
        var legacy = new LegacyR2R(resolver, imagePath);

        if (!legacy.ReadyToRunHeader.Sections.TryGetValue(LegacySectionType.CrossModuleInlineInfo, out var legacySection))
        {
            Assert.False(requireSection,
                $"Legacy reader did not see CrossModuleInlineInfo section in '{Path.GetFileName(imagePath)}'.");
            output.WriteLine($"[skip] {Path.GetFileName(imagePath)}: legacy reader did not see CrossModuleInlineInfo");
            return;
        }

        int legacyOffset = legacy.GetOffset(legacySection.RelativeVirtualAddress);
        var legacyXm = new LegacyCrossModule(legacy, legacyOffset, legacyOffset + legacySection.Size);
        List<LegacyCrossModule.InliningEntry> legacyEntries = legacyXm.GetEntries();

        CrossModuleInlineInfoTable strucTable = struc.GetCrossModuleInlineInfoTable(xmSection);

        Assert.True(strucTable.Entries.Count == legacyEntries.Count,
            $"Entry count mismatch in '{Path.GetFileName(imagePath)}': " +
            $"structural={strucTable.Entries.Count}, legacy={legacyEntries.Count}");

        var mismatches = new List<string>();
        for (int i = 0; i < legacyEntries.Count; i++)
        {
            var legacyEntry = legacyEntries[i];
            var strucEntry = strucTable.Entries[i];

            if (strucEntry.Inliners.Count != legacyEntry.Inliners.Count)
            {
                mismatches.Add($"  entry[{i}] inliner-count: structural={strucEntry.Inliners.Count}, legacy={legacyEntry.Inliners.Count}");
                continue;
            }

            for (int j = 0; j < legacyEntry.Inliners.Count; j++)
            {
                var lInliner = legacyEntry.Inliners[j];
                var sInliner = strucEntry.Inliners[j];
                if (lInliner.IsCrossModule || sInliner.IsCrossModule)
                    continue;
                if (lInliner.ModuleIndex != sInliner.ModuleIndex)
                {
                    mismatches.Add(
                        $"  entry[{i}].Inliners[{j}] ModuleIndex: structural={sInliner.ModuleIndex}, " +
                        $"legacy={lInliner.ModuleIndex} (inlinee module={legacyEntry.Inlinee.ModuleIndex}, rid={sInliner.Index})");
                    if (mismatches.Count >= 10)
                        break;
                }
            }
            if (mismatches.Count >= 10)
                break;
        }

        Assert.True(mismatches.Count == 0,
            $"CrossModuleInlineInfo ModuleIndex mismatches in '{Path.GetFileName(imagePath)}' " +
            $"(showing up to 10):{Environment.NewLine}{string.Join(Environment.NewLine, mismatches)}");
    }

    private sealed class NullTestOutput : ITestOutputHelper
    {
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }

    /// <summary>
    /// Minimal legacy resolver duplicated from <see cref="StructuralLegacyParity"/>'s
    /// internal helper — probes the image dir + supplied probe dirs and wraps located
    /// PE files in <see cref="LegacyStandaloneMetadata"/>.
    /// </summary>
    private sealed class ParityAssemblyResolver : LegacyIAsmResolver
    {
        private readonly string[] _probeDirs;
        private readonly Dictionary<string, LegacyIAsmMetadata?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ParityAssemblyResolver(string[] probeDirs)
        {
            _probeDirs = probeDirs;
        }

        public LegacyIAsmMetadata? FindAssembly(MetadataReader mr, AssemblyReferenceHandle h, string parentFile)
        {
            AssemblyReference asmRef = mr.GetAssemblyReference(h);
            return FindAssembly(mr.GetString(asmRef.Name), parentFile);
        }

        public LegacyIAsmMetadata? FindAssembly(string simpleName, string parentFile)
        {
            if (_cache.TryGetValue(simpleName, out LegacyIAsmMetadata? cached))
                return cached;

            foreach (string dir in Prepend(Path.GetDirectoryName(parentFile), _probeDirs))
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                foreach (string ext in new[] { ".dll", ".exe" })
                {
                    string candidate = Path.Combine(dir, simpleName + ext);
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            var pe = new PEReader(File.OpenRead(candidate));
                            if (pe.HasMetadata)
                            {
                                var metadata = new LegacyStandaloneMetadata(pe);
                                _cache[simpleName] = metadata;
                                return metadata;
                            }
                            pe.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }

            _cache[simpleName] = null;
            return null;
        }

        private static IEnumerable<string> Prepend(string? first, IEnumerable<string> rest)
        {
            if (!string.IsNullOrEmpty(first))
                yield return first;
            foreach (string s in rest)
                yield return s;
        }
    }
}
