// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using ILCompiler.Reflection.ReadyToRun;
using ILCompiler.Reflection.ReadyToRun.Format;
using Internal.Runtime;

using FormatReader = ILCompiler.Reflection.ReadyToRun.Format.ReadyToRunReader;

namespace R2RDump
{
    /// <summary>
    /// Dumps all R2R sections using the Format.ReadyToRunReader (structural only,
    /// no metadata resolution). Output is a human-readable CLI format.
    /// </summary>
    internal sealed class RawSectionsDumper
    {
        private readonly TextWriter _writer;
        private readonly FormatReader _reader;

        public RawSectionsDumper(FormatReader reader, TextWriter writer)
        {
            _reader = reader;
            _writer = writer;
        }

        public static FormatReader CreateFormatReader(string filename)
        {
            byte[] image = File.ReadAllBytes(filename);
            PEReader peReader = new PEReader(new MemoryStream(image));
            PEImageReader peImageReader = new PEImageReader(peReader);
            NativeReader nativeReader = new NativeReader(new MemoryStream(image));
            return new FormatReader(peImageReader, nativeReader);
        }

        public void Dump()
        {
            WriteDivider("R2R Raw Sections");

            _writer.WriteLine($"Machine:       {_reader.Machine}");
            _writer.WriteLine($"PointerSize:   {_reader.TargetPointerSize}");
            _writer.WriteLine($"Composite:     {_reader.Composite}");

            var header = _reader.ReadyToRunHeader;
            _writer.WriteLine($"Signature:     {header.SignatureString}");
            _writer.WriteLine($"Version:       {header.MajorVersion}.{header.MinorVersion}");
            _writer.WriteLine($"Flags:         0x{header.Flags:X8}");
            _writer.WriteLine($"Sections:      {header.Sections.Count}");
            _writer.WriteLine();

            // Enumerate all sections present in the header
            WriteDivider("Section Directory");
            foreach (var section in _reader.EnumerateAllSections())
            {
                _writer.WriteLine($"  [{(int)section.Type,3}] {section.Type,-40} RVA=0x{section.RelativeVirtualAddress:X8}  Size=0x{section.Size:X6}  Offset=0x{section.FileOffset:X8}");
            }
            _writer.WriteLine();

            // Dump each typed section
            DumpCompilerIdentifier();
            DumpOwnerCompositeExecutable();
            DumpRuntimeFunctions();
            DumpImportSections();
            DumpMethodDefEntryPoints();
            DumpExceptionInfo();
            DumpDebugInfo();
            DumpAvailableTypes();
            DumpInstanceMethodEntryPoints();
            DumpInliningInfo();
            DumpInliningInfo2();
            DumpPgoInstrumentationData();
            DumpCrossModuleInlineInfo();
            DumpHotColdMap();
            DumpMethodIsGenericMap();
            DumpEnclosingTypeMap();
            DumpTypeGenericInfoMap();
            DumpComponentAssemblies();
            DumpManifestAssemblyMvids();
            DumpManifestMetadata();

            // Dump any remaining opaque sections
            DumpOpaqueSections();
        }

        // ── Individual section dumpers ──────────────────────────────────

        private void DumpCompilerIdentifier()
        {
            var value = _reader.CompilerIdentifier;
            if (value is null) return;
            WriteDivider("CompilerIdentifier (100)");
            _writer.WriteLine($"  {value}");
            _writer.WriteLine();
        }

        private void DumpOwnerCompositeExecutable()
        {
            var value = _reader.OwnerCompositeExecutable;
            if (value is null) return;
            WriteDivider("OwnerCompositeExecutable (116)");
            _writer.WriteLine($"  {value}");
            _writer.WriteLine();
        }

        private void DumpRuntimeFunctions()
        {
            var table = _reader.RuntimeFunctions;
            if (table is null) return;
            WriteDivider($"RuntimeFunctions (102) [{table.Entries.Count} entries]");
            WriteTableHeader("Index", "StartRVA", "EndRVA", "UnwindRVA");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  {e.Index,6}  0x{e.StartRva:X8}  {(e.EndRva.HasValue ? $"0x{e.EndRva.Value:X8}" : "          ")}  0x{e.UnwindRva:X8}");
            }
            _writer.WriteLine();
        }

        private void DumpImportSections()
        {
            var table = _reader.ImportSections;
            if (table is null) return;
            WriteDivider($"ImportSections (101) [{table.Entries.Count} entries]");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  [{e.Index}] RVA=0x{e.SectionRva:X8}  Size=0x{e.SectionSize:X4}  Flags=0x{(ushort)e.Flags:X4}  Type={e.Type}  EntrySize={e.EntrySize}  Count={e.EntryCount}  SigRVA=0x{e.SignatureRva:X8}  AuxRVA=0x{e.AuxiliaryDataRva:X8}");
            }
            _writer.WriteLine();
        }

        private void DumpMethodDefEntryPoints()
        {
            var table = _reader.MethodDefEntryPoints;
            if (table is null) return;
            WriteDivider($"MethodDefEntryPoints (103) [{table.Entries.Count} entries]");
            WriteTableHeader("RID", "RuntimeFunc", "Fixups");
            foreach (var e in table.Entries)
            {
                string fixups = e.FixupCells.Count == 0
                    ? "(none)"
                    : string.Join(", ", FormatFixups(e.FixupCells));
                _writer.WriteLine($"  {e.Rid,6}  {e.EntryPointIndex,11}  {fixups}");
            }
            _writer.WriteLine();
        }

        private void DumpExceptionInfo()
        {
            var table = _reader.ExceptionInfo;
            if (table is null) return;
            WriteDivider($"ExceptionInfo (104) [{table.Entries.Count} entries]");
            WriteTableHeader("MethodRVA", "EhInfoRVA");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  0x{e.MethodRva:X8}  0x{e.EhInfoRva:X8}");
            }
            _writer.WriteLine();
        }

        private void DumpDebugInfo()
        {
            var table = _reader.DebugInfo;
            if (table is null) return;
            WriteDivider($"DebugInfo (105) [{table.Entries.Count} entries]");
            WriteTableHeader("RTFunc", "Offset");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  {e.RuntimeFunctionIndex,6}  0x{e.DebugInfoOffset:X8}");
            }
            _writer.WriteLine();
        }

        private void DumpAvailableTypes()
        {
            var table = _reader.AvailableTypes;
            if (table is null) return;
            WriteDivider($"AvailableTypes (108) [{table.Entries.Count} entries]");
            WriteTableHeader("RID", "Exported");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  {e.Rid,6}  {(e.IsExportedType ? "yes" : "no")}");
            }
            _writer.WriteLine();
        }

        private void DumpInstanceMethodEntryPoints()
        {
            var table = _reader.InstanceMethodEntryPoints;
            if (table is null) return;
            WriteDivider($"InstanceMethodEntryPoints (109) [{table.Entries.Count} entries]");
            WriteTableHeader("SigOffset", "Hash");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  0x{e.SignatureBlobOffset:X8}  0x{e.LowHashcode:X2}");
            }
            _writer.WriteLine();
        }

        private void DumpInliningInfo()
        {
            var table = _reader.InliningInfo;
            if (table is null) return;
            WriteDivider($"InliningInfo (110) [{table.Entries.Count} entries]");
            foreach (var e in table.Entries)
            {
                string inliners = string.Join(", ", e.InlinerRids);
                _writer.WriteLine($"  Inlinee RID {e.InlineeRid} <- [{inliners}]");
            }
            _writer.WriteLine();
        }

        private void DumpInliningInfo2()
        {
            var table = _reader.InliningInfo2;
            if (table is null) return;
            WriteDivider($"InliningInfo2 (114) [{table.Entries.Count} entries]");
            foreach (var e in table.Entries)
            {
                string mod = e.InlineeHasModule ? $" module={e.InlineeModuleIndex}" : "";
                _writer.Write($"  Inlinee RID {e.InlineeRid}{mod} <- [");
                for (int i = 0; i < e.Inliners.Count; i++)
                {
                    if (i > 0) _writer.Write(", ");
                    var inl = e.Inliners[i];
                    _writer.Write($"{inl.Rid}");
                    if (inl.HasModule) _writer.Write($"(mod={inl.ModuleIndex})");
                }
                _writer.WriteLine("]");
            }
            _writer.WriteLine();
        }

        private void DumpPgoInstrumentationData()
        {
            var table = _reader.PgoInstrumentationData;
            if (table is null) return;
            WriteDivider($"PgoInstrumentationData (117) [{table.Entries.Count} entries]");
            WriteTableHeader("SigOffset", "Hash");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  0x{e.SignatureBlobOffset:X8}  0x{e.LowHashcode:X2}");
            }
            _writer.WriteLine();
        }

        private void DumpCrossModuleInlineInfo()
        {
            var table = _reader.CrossModuleInlineInfo;
            if (table is null) return;
            WriteDivider($"CrossModuleInlineInfo (119) [{table.Entries.Count} entries]");
            foreach (var e in table.Entries)
            {
                string kind = e.IsCrossModuleInlinee ? "xmod" : "local";
                string mod = (!e.IsCrossModuleInlinee && e.InlineeModuleIndex != 0) ? $" module={e.InlineeModuleIndex}" : "";
                _writer.Write($"  Inlinee {kind} idx={e.InlineeIndex}{mod} <- [");
                for (int i = 0; i < e.Inliners.Count; i++)
                {
                    if (i > 0) _writer.Write(", ");
                    var inl = e.Inliners[i];
                    string inlKind = inl.IsCrossModule ? "xmod:" : "";
                    string inlMod = inl.ModuleIndex != 0 ? $"(mod={inl.ModuleIndex})" : "";
                    _writer.Write($"{inlKind}{inl.Index}{inlMod}");
                }
                _writer.WriteLine("]");
            }
            _writer.WriteLine();
        }

        private void DumpHotColdMap()
        {
            var table = _reader.HotColdMap;
            if (table is null) return;
            WriteDivider($"HotColdMap (120) [{table.Entries.Count} entries]");
            WriteTableHeader("Hot", "Cold");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  {e.HotRuntimeFunctionIndex,6}  {e.ColdRuntimeFunctionIndex,6}");
            }
            _writer.WriteLine();
        }

        private void DumpMethodIsGenericMap()
        {
            var map = _reader.MethodIsGenericMap;
            if (map is null) return;
            int genericCount = 0;
            for (int rid = 1; rid <= map.Count; rid++)
                if (map.IsGeneric(rid)) genericCount++;

            WriteDivider($"MethodIsGenericMap (121) [count={map.Count}, generic={genericCount}]");
            // Don't dump every bit — just summary + first few
            int shown = 0;
            for (int rid = 1; rid <= map.Count && shown < 20; rid++)
            {
                if (map.IsGeneric(rid))
                {
                    _writer.WriteLine($"  RID {rid}: generic");
                    shown++;
                }
            }
            if (genericCount > shown)
                _writer.WriteLine($"  ... and {genericCount - shown} more");
            _writer.WriteLine();
        }

        private void DumpEnclosingTypeMap()
        {
            var map = _reader.EnclosingTypeMap;
            if (map is null) return;
            int nestedCount = 0;
            for (int rid = 1; rid <= map.Count; rid++)
                if (map.GetEnclosingTypeRid(rid) != 0) nestedCount++;

            WriteDivider($"EnclosingTypeMap (122) [count={map.Count}, nested={nestedCount}]");
            int shown = 0;
            for (int rid = 1; rid <= map.Count && shown < 20; rid++)
            {
                int enclosing = map.GetEnclosingTypeRid(rid);
                if (enclosing != 0)
                {
                    _writer.WriteLine($"  TypeDef RID {rid} -> enclosing RID {enclosing}");
                    shown++;
                }
            }
            if (nestedCount > shown)
                _writer.WriteLine($"  ... and {nestedCount - shown} more");
            _writer.WriteLine();
        }

        private void DumpTypeGenericInfoMap()
        {
            var map = _reader.TypeGenericInfoMap;
            if (map is null) return;
            int genericCount = 0;
            for (int rid = 1; rid <= map.Count; rid++)
                if ((byte)map.GetInfo(rid) != 0) genericCount++;

            WriteDivider($"TypeGenericInfoMap (123) [count={map.Count}, nonzero={genericCount}]");
            int shown = 0;
            for (int rid = 1; rid <= map.Count && shown < 20; rid++)
            {
                var info = map.GetInfo(rid);
                if ((byte)info != 0)
                {
                    _writer.WriteLine($"  TypeDef RID {rid}: 0x{(byte)info:X1} ({FormatGenericInfo((byte)info)})");
                    shown++;
                }
            }
            if (genericCount > shown)
                _writer.WriteLine($"  ... and {genericCount - shown} more");
            _writer.WriteLine();
        }

        private void DumpComponentAssemblies()
        {
            var table = _reader.ComponentAssemblies;
            if (table is null) return;
            WriteDivider($"ComponentAssemblies (115) [{table.Entries.Count} entries]");
            foreach (var e in table.Entries)
            {
                _writer.WriteLine($"  [{e.Index}] CorHeader=0x{e.CorHeaderRva:X8}/{e.CorHeaderSize}  AsmHeader=0x{e.AssemblyHeaderRva:X8}/{e.AssemblyHeaderSize}");
            }
            _writer.WriteLine();
        }

        private void DumpManifestAssemblyMvids()
        {
            var mvids = _reader.ManifestAssemblyMvids;
            if (mvids is null) return;
            WriteDivider($"ManifestAssemblyMvids (118) [{mvids.Count} entries]");
            for (int i = 0; i < mvids.Count; i++)
            {
                _writer.WriteLine($"  [{i}] {mvids[i]}");
            }
            _writer.WriteLine();
        }

        private void DumpManifestMetadata()
        {
            var meta = _reader.ManifestMetadata;
            if (meta is null) return;
            WriteDivider("ManifestMetadata (112)");
            _writer.WriteLine($"  FileOffset=0x{meta.FileOffset:X8}  Size=0x{meta.Size:X6}");
            _writer.WriteLine();
        }

        private void DumpOpaqueSections()
        {
            // Dump sections that have no typed parser
            ReadyToRunSectionType[] opaqueTypes = new[]
            {
                ReadyToRunSectionType.DelayLoadMethodCallThunks,
                ReadyToRunSectionType.ProfileDataInfo,
                ReadyToRunSectionType.AttributePresence,
                ReadyToRunSectionType.ExternalTypeMaps,
                ReadyToRunSectionType.ProxyTypeMaps,
                ReadyToRunSectionType.TypeMapAssemblyTargets,
            };

            foreach (var type in opaqueTypes)
            {
                var section = _reader.GetOpaqueSection(type);
                if (section is null) continue;
                WriteDivider($"{section.Type} ({(int)section.Type}) [opaque]");
                _writer.WriteLine($"  RVA=0x{section.RelativeVirtualAddress:X8}  Size=0x{section.Size:X6}  FileOffset=0x{section.FileOffset:X8}");
                _writer.WriteLine();
            }
        }

        // ── Formatting helpers ──────────────────────────────────────────

        private void WriteDivider(string title)
        {
            int len = Math.Max(72 - title.Length - 2, 2);
            _writer.WriteLine(new string('=', len / 2) + " " + title + " " + new string('=', (len + 1) / 2));
        }

        private void WriteTableHeader(params string[] columns)
        {
            _writer.Write(" ");
            foreach (var col in columns)
                _writer.Write($" {col,-11}");
            _writer.WriteLine();
        }

        private static string FormatOptionalRva(int? rva) =>
            rva.HasValue ? $"0x{rva.Value:X8}" : "          ";

        private static IEnumerable<string> FormatFixups(IReadOnlyList<FixupCellRef> fixups)
        {
            foreach (var f in fixups)
                yield return $"T{f.TableIndex}[{f.CellIndex}]";
        }

        private static string FormatGenericInfo(byte info)
        {
            var parts = new List<string>();
            if ((info & 0x1) != 0) parts.Add("HasVariance");
            if ((info & 0x2) != 0) parts.Add("HasConstraints");
            if ((info & 0x4) != 0) parts.Add("HasGenericParams");
            return parts.Count > 0 ? string.Join("|", parts) : "0";
        }
    }
}
