// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias legacy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using ILCompiler.Reflection.ReadyToRun;

using StructReader = ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;
using StructSectionType = Internal.Runtime.ReadyToRunSectionType;

using LegacyReader = legacy::ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;
using LegacyIAsmResolver = legacy::ILCompiler.Reflection.ReadyToRun.IAssemblyResolver;
using LegacyIAsmMetadata = legacy::ILCompiler.Reflection.ReadyToRun.IAssemblyMetadata;
using LegacyStandaloneMetadata = legacy::ILCompiler.Reflection.ReadyToRun.StandaloneAssemblyMetadata;

namespace ILCompiler.ReadyToRun.Tests
{
    /// <summary>
    /// Opens an R2R image with both the legacy <see cref="LegacyReader"/> and the new
    /// Structural <see cref="StructReader"/>, and asserts that the parsed surfaces agree.
    /// Used as an oracle-based correctness check for the Structural reader on every image
    /// produced by the test corpus.
    /// </summary>
    internal static class StructuralLegacyParity
    {
        public static void AssertParity(string imagePath, params string[] extraProbeDirs)
        {
            var diffs = new List<string>();

            byte[] bytes = File.ReadAllBytes(imagePath);
            using var peReader = new PEReader(new MemoryStream(bytes));
            using var nativeReader = new NativeReader(new MemoryStream(bytes));
            using var struc = new StructReader(new PEImageReader(peReader, leaveOpen: true), nativeReader, imagePath);

            var resolver = new ParityAssemblyResolver(new[] { Path.GetDirectoryName(imagePath)! }.Concat(extraProbeDirs ?? Array.Empty<string>()).ToArray());
            var legacyRdr = new LegacyReader(resolver, imagePath);

            CompareHeader(struc, legacyRdr, diffs);
            CompareMachine(struc, legacyRdr, diffs);
            CompareComposite(struc, legacyRdr, diffs);
            ComparePointerSize(struc, legacyRdr, diffs);
            CompareSections(struc, legacyRdr, diffs);

            if (diffs.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Structural vs legacy parity mismatch for '{Path.GetFileName(imagePath)}':{Environment.NewLine}"
                    + string.Join(Environment.NewLine, diffs));
            }
        }

        private static void CompareHeader(StructReader s, LegacyReader l, List<string> diffs)
        {
            var sh = s.ReadyToRunHeader;
            var lh = l.ReadyToRunHeader;

            if (sh.Signature != lh.Signature)
                diffs.Add($"Header.Signature: structural=0x{sh.Signature:X8} legacy=0x{lh.Signature:X8}");
            if (sh.MajorVersion != lh.MajorVersion)
                diffs.Add($"Header.MajorVersion: structural={sh.MajorVersion} legacy={lh.MajorVersion}");
            if (sh.MinorVersion != lh.MinorVersion)
                diffs.Add($"Header.MinorVersion: structural={sh.MinorVersion} legacy={lh.MinorVersion}");
            if (sh.Flags != lh.Flags)
                diffs.Add($"Header.Flags: structural=0x{sh.Flags:X8} legacy=0x{lh.Flags:X8}");
        }

        private static void CompareMachine(StructReader s, LegacyReader l, List<string> diffs)
        {
            // Both surface System.Reflection.PortableExecutable.Machine.
            if ((int)s.Machine != (int)l.Machine)
                diffs.Add($"Machine: structural={s.Machine} legacy={l.Machine}");
        }

        private static void CompareComposite(StructReader s, LegacyReader l, List<string> diffs)
        {
            if (s.Composite != l.Composite)
                diffs.Add($"Composite: structural={s.Composite} legacy={l.Composite}");
        }

        private static void ComparePointerSize(StructReader s, LegacyReader l, List<string> diffs)
        {
            if (s.TargetPointerSize != l.TargetPointerSize)
                diffs.Add($"TargetPointerSize: structural={s.TargetPointerSize} legacy={l.TargetPointerSize}");
        }

        private static void CompareSections(StructReader s, LegacyReader l, List<string> diffs)
        {
            var structuralSections = s.GetSections()
                .Select(h => ((int)h.Type, (int)h.RelativeVirtualAddress, h.Size))
                .OrderBy(t => t.Item1)
                .ToList();

            var legacySections = l.ReadyToRunHeader.Sections
                .Select(kv => ((int)kv.Key, kv.Value.RelativeVirtualAddress, kv.Value.Size))
                .OrderBy(t => t.Item1)
                .ToList();

            if (structuralSections.Count != legacySections.Count)
            {
                diffs.Add($"Sections.Count: structural={structuralSections.Count} legacy={legacySections.Count}");
            }

            var structuralByType = structuralSections.ToDictionary(t => t.Item1, t => (t.Item2, t.Size));
            var legacyByType = legacySections.ToDictionary(t => t.Item1, t => (t.Item2, t.Size));

            foreach (int type in structuralByType.Keys.Union(legacyByType.Keys).OrderBy(x => x))
            {
                bool inStruct = structuralByType.TryGetValue(type, out var sv);
                bool inLegacy = legacyByType.TryGetValue(type, out var lv);
                string name = ((StructSectionType)type).ToString();
                if (!inStruct)
                {
                    diffs.Add($"Sections[{name}]: missing in structural (legacy RVA=0x{lv.Item1:X}, Size={lv.Size})");
                    continue;
                }
                if (!inLegacy)
                {
                    diffs.Add($"Sections[{name}]: missing in legacy (structural RVA=0x{sv.Item1:X}, Size={sv.Size})");
                    continue;
                }
                if (sv.Item1 != lv.Item1 || sv.Size != lv.Size)
                {
                    diffs.Add($"Sections[{name}]: structural=(RVA=0x{sv.Item1:X}, Size={sv.Size}) legacy=(RVA=0x{lv.Item1:X}, Size={lv.Size})");
                }
            }
        }

        /// <summary>
        /// Minimal legacy resolver that probes a fixed set of directories (image dir, runtime pack,
        /// and IL output dir) and wraps located PE files in <see cref="LegacyStandaloneMetadata"/>.
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
                                // Probe next candidate.
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
}
