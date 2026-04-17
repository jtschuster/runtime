// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

using StructuralReader = ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;

namespace ILCompiler.Reflection.ReadyToRun.Assertions
{
    /// <summary>
    /// Test-helper library that exposes predicate-style <c>HasX(..., out string reason)</c>
    /// checks about a ReadyToRun image. Built on top of the Structural reader.
    ///
    /// Usage:
    /// <code>
    /// using var r2r = R2RAssertions.Open(path, runtimePackDir);
    /// Assert.True(r2r.HasManifestRef("System.Runtime", out string reason), reason);
    /// </code>
    /// </summary>
    public sealed class R2RAssertions : IDisposable
    {
        private readonly StructuralReader _reader;
        private readonly NativeReader _nativeReader;
        private readonly PEReader _peReader;
        private readonly AssertionsAssemblyResolver _resolver;
        private readonly Lazy<MethodInventory> _inventory;
        private bool _disposed;

        private R2RAssertions(
            StructuralReader reader,
            NativeReader nativeReader,
            PEReader peReader,
            AssertionsAssemblyResolver resolver)
        {
            _reader = reader;
            _nativeReader = nativeReader;
            _peReader = peReader;
            _resolver = resolver;
            _inventory = new Lazy<MethodInventory>(() => MethodInventory.Build(_reader, _nativeReader, _resolver));
            _ilBodyMethodByImportIndex = new Lazy<Dictionary<uint, MethodSignature>>(BuildILBodyMap);
        }

        /// <summary>
        /// Open an R2R image from a file path. <paramref name="extraProbeDirs"/> are
        /// additional directories searched when resolving referenced assemblies.
        /// </summary>
        public static R2RAssertions Open(string path, params string[] extraProbeDirs)
        {
            if (path is null)
                throw new ArgumentNullException(nameof(path));
            byte[] bytes = File.ReadAllBytes(path);
            return CreateCore(bytes, path, extraProbeDirs);
        }

        /// <summary>
        /// Open an R2R image from a pre-loaded byte buffer. <paramref name="name"/>
        /// is used for diagnostics and as the logical image path for probing.
        /// </summary>
        public static R2RAssertions FromBytes(byte[] imageBytes, string name, params string[] extraProbeDirs)
        {
            if (imageBytes is null)
                throw new ArgumentNullException(nameof(imageBytes));
            return CreateCore(imageBytes, name, extraProbeDirs);
        }

        private static R2RAssertions CreateCore(byte[] imageBytes, string filename, string[] extraProbeDirs)
        {
            bool isMachO = imageBytes.Length >= 4
                && imageBytes[0] == 0xCF && imageBytes[1] == 0xFA && imageBytes[2] == 0xED && imageBytes[3] == 0xFE;

            PEReader peReader = null;
            IBinaryImageReader imageReader;
            if (isMachO)
            {
                imageReader = new MachO.MachOImageReader(imageBytes);
            }
            else
            {
                peReader = new PEReader(new MemoryStream(imageBytes));
                imageReader = new PEImageReader(peReader);
            }

            NativeReader nativeReader = null;
            StructuralReader structural = null;
            AssertionsAssemblyResolver resolver = null;
            try
            {
                nativeReader = new NativeReader(new MemoryStream(imageBytes));
                structural = new StructuralReader(imageReader, nativeReader, filename);
                resolver = new AssertionsAssemblyResolver(structural, imageReader, filename, extraProbeDirs);
                return new R2RAssertions(structural, nativeReader, peReader, resolver);
            }
            catch
            {
                resolver?.Dispose();
                structural?.Dispose();
                nativeReader?.Dispose();
                peReader?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _resolver?.Dispose();
            _reader?.Dispose();
            _nativeReader?.Dispose();
            _peReader?.Dispose();
        }

        // ---- Manifest / metadata ----
        public bool HasManifestRef(string assemblyName, out string reason)
        {
            if (string.IsNullOrEmpty(assemblyName))
            {
                reason = "assemblyName is empty";
                return false;
            }
            foreach (string name in _resolver.ModuleNames)
            {
                if (name is null)
                    continue;
                // Strip " (self)" / " (manifest)" / " (unresolved #N)" suffixes for comparison.
                int parenIdx = name.IndexOf(" (");
                string bare = parenIdx >= 0 ? name[..parenIdx] : name;
                if (string.Equals(bare, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"found manifest/assembly ref '{name}'";
                    return true;
                }
            }
            reason = $"no manifest/assembly ref named '{assemblyName}' found among [{string.Join(", ", _resolver.ModuleNames)}]";
            return false;
        }
        public bool HasManifestRef(string assemblyName) => HasManifestRef(assemblyName, out _);

        // ---- Inlining ----
        public bool HasInlinedMethod(string inlinerMethodName, string inlineeMethodName, out string reason)
        {
            // Searches all inlining tables (per-module InliningInfo, per-component InliningInfo2,
            // and CrossModuleInlineInfo) for an inliner→inlinee pair matching the supplied names.
            foreach ((string inliner, string inlinee) in EnumerateInliningPairs())
            {
                if (inliner.Contains(inlinerMethodName, StringComparison.Ordinal)
                    && inlinee.Contains(inlineeMethodName, StringComparison.Ordinal))
                {
                    reason = $"found inlining: {inliner} inlined {inlinee}";
                    return true;
                }
            }
            reason = $"no inlining of '{inlineeMethodName}' by '{inlinerMethodName}' found";
            return false;
        }
        public bool HasInlinedMethod(string inlinerMethodName, string inlineeMethodName) => HasInlinedMethod(inlinerMethodName, inlineeMethodName, out _);

        public bool HasCrossModuleInliningInfo(out string reason)
        {
            foreach (ReadyToRunSectionHandle s in _reader.GetSections())
            {
                if (s.Type == ReadyToRunSectionType.CrossModuleInlineInfo)
                {
                    reason = "CrossModuleInlineInfo section is present";
                    return true;
                }
            }
            reason = "no CrossModuleInlineInfo section";
            return false;
        }
        public bool HasCrossModuleInliningInfo() => HasCrossModuleInliningInfo(out _);

        public bool HasCrossModuleInlinedMethod(string inlinerMethodName, string inlineeMethodName, out string reason)
        {
            var observed = new List<string>();
            foreach (ReadyToRunSectionHandle s in _reader.GetSections())
            {
                if (s.Type != ReadyToRunSectionType.CrossModuleInlineInfo)
                    continue;
                CrossModuleInlineInfoTable table = _reader.GetCrossModuleInlineInfoTable(s);
                foreach (CrossModuleInlineEntry entry in table.Entries)
                {
                    string inlinee = FormatCrossModuleInlinee(entry);
                    foreach (CrossModuleInlinerRef inliner in entry.Inliners)
                    {
                        string inlinerName = FormatCrossModuleInliner(inliner);
                        observed.Add($"{inlinerName} -> {inlinee}");
                        if (inlinee.Contains(inlineeMethodName, StringComparison.Ordinal) &&
                            inlinerName.Contains(inlinerMethodName, StringComparison.Ordinal))
                        {
                            reason = $"found cross-module inlining: {inlinerName} inlined {inlinee}";
                            return true;
                        }
                    }
                }
            }
            reason = $"no cross-module inlining of '{inlineeMethodName}' by '{inlinerMethodName}' found. Observed: [{string.Join("; ", observed)}]";
            return false;
        }
        public bool HasCrossModuleInlinedMethod(string inlinerMethodName, string inlineeMethodName) => HasCrossModuleInlinedMethod(inlinerMethodName, inlineeMethodName, out _);

        public bool HasCrossModuleInliners(string inlineeMethodName, IReadOnlyList<string> expectedInlinerNames, out string reason)
        {
            if (expectedInlinerNames is null || expectedInlinerNames.Count == 0)
            {
                reason = "expectedInlinerNames is empty";
                return false;
            }
            foreach (ReadyToRunSectionHandle s in _reader.GetSections())
            {
                if (s.Type != ReadyToRunSectionType.CrossModuleInlineInfo)
                    continue;
                CrossModuleInlineInfoTable table = _reader.GetCrossModuleInlineInfoTable(s);
                foreach (CrossModuleInlineEntry entry in table.Entries)
                {
                    string inlinee = FormatCrossModuleInlinee(entry);
                    if (!inlinee.Contains(inlineeMethodName, StringComparison.Ordinal))
                        continue;
                    var inlinerNames = new List<string>();
                    foreach (CrossModuleInlinerRef inliner in entry.Inliners)
                        inlinerNames.Add(FormatCrossModuleInliner(inliner));

                    bool allFound = true;
                    foreach (string expected in expectedInlinerNames)
                    {
                        bool found = false;
                        foreach (string got in inlinerNames)
                        {
                            if (got.Contains(expected, StringComparison.Ordinal))
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            allFound = false;
                            break;
                        }
                    }
                    if (allFound)
                    {
                        reason = $"found all expected inliners for {inlinee}: [{string.Join(", ", inlinerNames)}]";
                        return true;
                    }
                }
            }
            reason = $"no CrossModuleInlineInfo entry for '{inlineeMethodName}' with inliners [{string.Join(", ", expectedInlinerNames)}]";
            return false;
        }
        public bool HasCrossModuleInliners(string inlineeMethodName, IReadOnlyList<string> expectedInlinerNames) => HasCrossModuleInliners(inlineeMethodName, expectedInlinerNames, out _);

        // ---- Async / resumption ----
        public bool HasAsyncVariant(string methodName, out string reason)
        {
            var observed = new List<string>();
            foreach (MethodInventoryEntry m in _inventory.Value.Methods)
            {
                if (m.IsAsync)
                    observed.Add(m.SimpleName);
                if (m.IsAsync && m.SimpleName.Contains(methodName, StringComparison.Ordinal))
                {
                    reason = $"found async variant: {m.Signature}";
                    return true;
                }
            }
            reason = $"no async variant found for '{methodName}'. Observed async methods: [{string.Join(", ", observed)}]. Total methods: {_inventory.Value.Methods.Count}. All methods: [{string.Join(", ", _inventory.Value.Methods.Select(m => $"{m.SimpleName}(async={m.IsAsync})"))}]";
            return false;
        }
        public bool HasAsyncVariant(string methodName) => HasAsyncVariant(methodName, out _);

        public bool HasResumptionStub(string methodName, out string reason)
        {
            foreach (MethodInventoryEntry m in _inventory.Value.Methods)
            {
                if (m.IsResumeStub && m.SimpleName.Contains(methodName, StringComparison.Ordinal))
                {
                    reason = $"found resumption stub: {m.Signature}";
                    return true;
                }
            }
            reason = $"no resumption stub found for '{methodName}'";
            return false;
        }
        public bool HasResumptionStub(string methodName) => HasResumptionStub(methodName, out _);

        // ---- Fixup kinds ----
        public bool HasContinuationLayout(out string reason) => HasFixupKind(ReadyToRunFixupKind.ContinuationLayout, out reason);
        public bool HasContinuationLayout() => HasContinuationLayout(out _);

        public bool HasContinuationLayout(string methodName, out string reason) => HasFixupKindOnMethod(ReadyToRunFixupKind.ContinuationLayout, methodName, out reason);
        public bool HasContinuationLayout(string methodName) => HasContinuationLayout(methodName, out _);

        public bool HasResumptionStubFixup(out string reason) => HasFixupKind(ReadyToRunFixupKind.ResumptionStubEntryPoint, out reason);
        public bool HasResumptionStubFixup() => HasResumptionStubFixup(out _);

        public bool HasResumptionStubFixup(string methodName, out string reason) => HasFixupKindOnMethod(ReadyToRunFixupKind.ResumptionStubEntryPoint, methodName, out reason);
        public bool HasResumptionStubFixup(string methodName) => HasResumptionStubFixup(methodName, out _);

        public bool HasFixupKind(ReadyToRunFixupKind kind, out string reason)
        {
            foreach (MethodInventoryEntry m in _inventory.Value.Methods)
            {
                foreach (FixupInfo f in m.Fixups)
                {
                    if (f.Kind == kind)
                    {
                        reason = $"found fixup {kind} on {m.Signature}";
                        return true;
                    }
                }
            }
            reason = $"no fixup of kind {kind} found";
            return false;
        }
        public bool HasFixupKind(ReadyToRunFixupKind kind) => HasFixupKind(kind, out _);

        public bool HasFixupKindOnMethod(ReadyToRunFixupKind kind, string methodName, out string reason)
        {
            foreach (MethodInventoryEntry m in _inventory.Value.Methods)
            {
                if (!m.SimpleName.Contains(methodName, StringComparison.Ordinal))
                    continue;
                foreach (FixupInfo f in m.Fixups)
                {
                    if (f.Kind == kind)
                    {
                        reason = $"found fixup {kind} on {m.Signature}";
                        return true;
                    }
                }
            }
            reason = $"no fixup of kind {kind} on a method named like '{methodName}'";
            return false;
        }
        public bool HasFixupKindOnMethod(ReadyToRunFixupKind kind, string methodName) => HasFixupKindOnMethod(kind, methodName, out _);

        // ─── Inlining helpers ──────────────────────────────────────────────

        private IEnumerable<(string inliner, string inlinee)> EnumerateInliningPairs()
        {
            foreach (ReadyToRunSectionHandle s in _reader.GetSections())
            {
                switch (s.Type)
                {
                    case ReadyToRunSectionType.InliningInfo:
                        {
                            InliningInfoTable table = _reader.GetInliningInfoTable(s);
                            foreach (InliningInfoEntry e in table.Entries)
                            {
                                string inlinee = MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader(0), e.InlineeRid);
                                foreach (int inlinerRid in e.InlinerRids)
                                {
                                    string inliner = MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader(0), inlinerRid);
                                    yield return (inliner, inlinee);
                                }
                            }
                            break;
                        }
                    case ReadyToRunSectionType.InliningInfo2:
                        foreach (var pair in EnumerateInliningInfo2Pairs(s, ownerModuleIndex: 0))
                            yield return pair;
                        break;
                    case ReadyToRunSectionType.CrossModuleInlineInfo:
                        {
                            CrossModuleInlineInfoTable table = _reader.GetCrossModuleInlineInfoTable(s);
                            foreach (CrossModuleInlineEntry e in table.Entries)
                            {
                                string inlinee = FormatCrossModuleInlinee(e);
                                foreach (CrossModuleInlinerRef inliner in e.Inliners)
                                    yield return (FormatCrossModuleInliner(inliner), inlinee);
                            }
                            break;
                        }
                }
            }

            // Per-component InliningInfo2 for composite images.
            if (_reader.Composite)
            {
                foreach (var pair in EnumeratePerComponentInliningInfo2())
                    yield return pair;
            }
        }

        private IEnumerable<(string inliner, string inlinee)> EnumerateInliningInfo2Pairs(ReadyToRunSectionHandle section, int ownerModuleIndex)
        {
            InliningInfo2Table table = _reader.GetInliningInfo2Table(section);
            foreach (InliningInfo2Entry entry in table.Entries)
            {
                int inlineeModule = entry.InlineeHasModule ? (int)entry.InlineeModuleIndex : ownerModuleIndex;
                string inlinee = MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader(inlineeModule), entry.InlineeRid);
                foreach (InlinerRef inlinerRef in entry.Inliners)
                {
                    int inlinerModule = inlinerRef.HasModule ? (int)inlinerRef.ModuleIndex : ownerModuleIndex;
                    string inliner = MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader(inlinerModule), inlinerRef.Rid);
                    yield return (inliner, inlinee);
                }
            }
        }

        private IEnumerable<(string inliner, string inlinee)> EnumeratePerComponentInliningInfo2()
        {
            ReadyToRunSectionHandle? componentAssembliesHandle = null;
            foreach (ReadyToRunSectionHandle s in _reader.GetSections())
            {
                if (s.Type == ReadyToRunSectionType.ComponentAssemblies)
                {
                    componentAssembliesHandle = s;
                    break;
                }
            }
            if (componentAssembliesHandle is null)
                yield break;

            ComponentAssembliesTable componentTable = _reader.GetComponentAssembliesTable(componentAssembliesHandle.Value);
            for (int componentIdx = 0; componentIdx < componentTable.Entries.Count; componentIdx++)
            {
                ComponentAssemblyEntry componentEntry = componentTable.Entries[componentIdx];
                if (componentEntry.AssemblyHeaderRva == 0 || componentEntry.AssemblyHeaderSize == 0)
                    continue;
                int moduleIndex = componentIdx + _resolver.ComponentAssemblyIndexOffset;
                int headerOffset = _reader.GetOffsetForRVA(componentEntry.AssemblyHeaderRva);
                ReadyToRunCoreHeader coreHeader;
                try
                {
                    coreHeader = _reader.ReadReadyToRunCoreHeader(ref headerOffset);
                }
                catch
                {
                    continue;
                }

                foreach (ReadyToRunSectionHandle cs in coreHeader.Sections)
                {
                    if (cs.Type == ReadyToRunSectionType.InliningInfo2)
                    {
                        foreach (var pair in EnumerateInliningInfo2Pairs(cs, moduleIndex))
                            yield return pair;
                    }
                }
            }
        }

        private string FormatCrossModuleInlinee(CrossModuleInlineEntry entry)
        {
            if (entry.IsCrossModuleInlinee)
                return ResolveILBodyImport(entry.InlineeIndex);
            return MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader((int)entry.InlineeModuleIndex), (int)entry.InlineeIndex);
        }

        private string FormatCrossModuleInliner(CrossModuleInlinerRef inliner)
        {
            if (inliner.IsCrossModule)
                return ResolveILBodyImport(inliner.Index);
            return MethodInventory.SafeFormatMethodDef(_resolver.GetMetadataReader((int)inliner.ModuleIndex), (int)inliner.Index);
        }

        private string ResolveILBodyImport(uint importIndex)
        {
            Dictionary<uint, MethodSignature> map = _ilBodyMethodByImportIndex.Value;
            if (map.TryGetValue(importIndex, out MethodSignature sig))
                return MethodInventory.FormatMethodFromSignature(sig, _resolver);
            return $"<ILBody import #{importIndex}>";
        }

        private readonly Lazy<Dictionary<uint, MethodSignature>> _ilBodyMethodByImportIndex;

        private Dictionary<uint, MethodSignature> BuildILBodyMap()
        {
            var map = new Dictionary<uint, MethodSignature>();
            foreach (ImportSectionSignatures section in _inventory.Value.ImportSections)
            {
                bool isILBodySection = false;
                foreach (R2RFixupSignature sig in section.Signatures)
                {
                    if (sig is null) continue;
                    if (sig.Kind is ReadyToRunFixupKind.Check_IL_Body or ReadyToRunFixupKind.Verify_IL_Body)
                    {
                        isILBodySection = true;
                        break;
                    }
                }
                if (!isILBodySection)
                    continue;

                for (int i = 0; i < section.Signatures.Length; i++)
                {
                    R2RFixupSignature sig = section.Signatures[i];
                    if (sig?.Payload is R2RILBodyFixupPayload ilBody && ilBody.Method is not null)
                        map[(uint)i] = ilBody.Method;
                }
                return map;
            }
            return map;
        }
    }
}
