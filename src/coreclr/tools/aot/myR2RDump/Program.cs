// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using ILCompiler.Reflection.ReadyToRun.Structural;
using Internal.CorConstants;
using Internal.ReadyToRunConstants;
using Internal.Runtime;

using StructuralReader = ILCompiler.Reflection.ReadyToRun.Structural.ReadyToRunReader;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: myR2RDump <path-to-r2r-image>");
    return 1;
}

string filename = args[0];
using PEReader peReader = new PEReader(File.OpenRead(filename));
PEImageReader peImageReader = new PEImageReader(peReader);
using NativeReader nativeReader = new NativeReader(File.OpenRead(filename));
using StructuralReader reader = new StructuralReader(peImageReader, nativeReader, filename);
using DiskAssemblyResolver resolver = new DiskAssemblyResolver(reader, peReader, filename);

Console.WriteLine($"Image: {Path.GetFileName(filename)}");
Console.WriteLine($"Machine: {reader.Machine}  Composite: {reader.Composite}");
Console.WriteLine();

// Module reference table
Console.WriteLine("=== Module Reference Table ===");
for (int i = 0; i < resolver.ModuleCount; i++)
{
    var mr = resolver.GetMetadataReader(i);
    string hasMd = mr is not null ? $" [metadata loaded, MemberRef={mr.GetTableRowCount(TableIndex.MemberRef)}, MethodDef={mr.GetTableRowCount(TableIndex.MethodDef)}]" : "";
    Console.WriteLine($"  #{i} = {resolver.ModuleNames[i]}{hasMd}");
}
Console.WriteLine();

// Dispatch all sections
foreach (ReadyToRunSectionHandle section in reader.GetSections())
{
    try
    {
        DumpSection(section, reader, nativeReader, resolver);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"=== {section.Type} (RVA=0x{(int)section.RelativeVirtualAddress:X}, Size={section.Size}) ===");
        Console.WriteLine($"  (error dumping: {ex.Message})");
        Console.WriteLine();
    }
}

// For composite images, also dump per-component assembly sections
if (reader.Composite)
{
    DumpPerComponentSections(reader, resolver);
}

return 0;

// ─── Section dispatch ───────────────────────────────────────────────────────

static void DumpSection(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    NativeReader nativeReader,
    DiskAssemblyResolver resolver)
{
    switch (section.Type)
    {
        case ReadyToRunSectionType.CompilerIdentifier:
            DumpCompilerIdentifier(section, reader);
            break;
        case ReadyToRunSectionType.OwnerCompositeExecutable:
            DumpOwnerCompositeExecutable(section, reader);
            break;
        case ReadyToRunSectionType.RuntimeFunctions:
            DumpRuntimeFunctions(section, reader);
            break;
        case ReadyToRunSectionType.MethodDefEntryPoints:
            DumpMethodDefEntryPoints(section, reader, resolver);
            break;
        case ReadyToRunSectionType.InstanceMethodEntryPoints:
            DumpInstanceMethodEntryPoints(section, reader, nativeReader, resolver);
            break;
        case ReadyToRunSectionType.ImportSections:
            DumpImportSections(section, reader, nativeReader, resolver);
            break;
        case ReadyToRunSectionType.ExceptionInfo:
            DumpExceptionInfo(section, reader);
            break;
        case ReadyToRunSectionType.DebugInfo:
            DumpDebugInfo(section, reader);
            break;
        case ReadyToRunSectionType.AvailableTypes:
            DumpAvailableTypes(section, reader, resolver);
            break;
        case ReadyToRunSectionType.ManifestMetadata:
            DumpManifestMetadata(section, reader);
            break;
        case ReadyToRunSectionType.InliningInfo:
            DumpInliningInfo(section, reader, resolver);
            break;
        case ReadyToRunSectionType.InliningInfo2:
            DumpInliningInfo2(section, reader, resolver);
            break;
        case ReadyToRunSectionType.CrossModuleInlineInfo:
            DumpCrossModuleInlineInfo(section, reader, resolver);
            break;
        case ReadyToRunSectionType.ComponentAssemblies:
            DumpComponentAssemblies(section, reader);
            break;
        case ReadyToRunSectionType.HotColdMap:
            DumpHotColdMap(section, reader);
            break;
        case ReadyToRunSectionType.PgoInstrumentationData:
            DumpPgoInstrumentationData(section, reader);
            break;
        case ReadyToRunSectionType.MethodIsGenericMap:
            DumpMethodIsGenericMap(section, reader, resolver);
            break;
        case ReadyToRunSectionType.EnclosingTypeMap:
            DumpEnclosingTypeMap(section, reader, resolver);
            break;
        case ReadyToRunSectionType.TypeGenericInfoMap:
            DumpTypeGenericInfoMap(section, reader, resolver);
            break;
        default:
            DumpRawSection(section);
            break;
    }
}

// ─── Per-section dump methods ───────────────────────────────────────────────

static void DumpCompilerIdentifier(ReadyToRunSectionHandle section, StructuralReader reader)
{
    Console.WriteLine("=== CompilerIdentifier ===");
    Console.WriteLine($"  {reader.GetCompilerIdentifier(section)}");
    Console.WriteLine();
}

static void DumpOwnerCompositeExecutable(ReadyToRunSectionHandle section, StructuralReader reader)
{
    Console.WriteLine("=== OwnerCompositeExecutable ===");
    Console.WriteLine($"  {reader.GetOwnerCompositeExecutable(section)}");
    Console.WriteLine();
}

static void DumpRuntimeFunctions(ReadyToRunSectionHandle section, StructuralReader reader)
{
    RuntimeFunctionsTable table = reader.GetRuntimeFunctionsTable(section);
    Console.WriteLine($"=== RuntimeFunctions ({table.Entries.Count}) ===");
    foreach (RuntimeFunctionEntry entry in table.Entries)
    {
        string end = entry.EndRva.HasValue ? $" End=0x{(int)entry.EndRva.Value:X8}" : "";
        Console.WriteLine($"  [{entry.Index,4}] Start=0x{(int)entry.StartRva:X8}{end} Unwind=0x{(int)entry.UnwindRva:X8}");
    }
    Console.WriteLine();
}

static void DumpMethodDefEntryPoints(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver,
    MetadataReader mdReaderOverride = null,
    string headerPrefix = null)
{
    MethodDefEntryPointsTable table = reader.GetMethodDefEntryPointsTable(section);
    MetadataReader mdReader = mdReaderOverride ?? resolver.GetMetadataReader(0);
    string prefix = headerPrefix is not null ? $"{headerPrefix} " : "";
    Console.WriteLine($"=== {prefix}MethodDefEntryPoints ({table.Entries.Count}) ===");
    foreach (MethodDefEntry entry in table.Entries)
    {
        string name = SafeFormatMethodDef(mdReader, (int)entry.Rid);
        string fixups = "";
        if (entry.FixupCells.Count > 0)
        {
            var parts = new List<string>(entry.FixupCells.Count);
            foreach (FixupCellRef cell in entry.FixupCells)
                parts.Add($"T{cell.TableIndex}:C{cell.CellIndex}");
            fixups = $" Fixups=[{string.Join(", ", parts)}]";
        }
        Console.WriteLine($"  RID={entry.Rid,-6} RF={entry.EntryPointIndex,-6} {name}{fixups}");
    }
    Console.WriteLine();
}

static void DumpInstanceMethodEntryPoints(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    NativeReader nativeReader,
    DiskAssemblyResolver resolver)
{
    InstanceMethodEntryPointsTable table = reader.GetInstanceMethodEntryPointsTable(section);
    Console.WriteLine($"=== InstanceMethodEntryPoints ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (InstanceMethodEntry entry in table.Entries)
    {
        string decoded = DecodeSignatureBlobAsMethod(nativeReader, entry.SignatureBlobOffset, resolver);
        Console.WriteLine($"  [{idx,4}] SigOffset=0x{entry.SignatureBlobOffset:X8} Hash=0x{entry.LowHashcode:X8} {decoded}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpImportSections(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    NativeReader nativeReader,
    DiskAssemblyResolver resolver)
{
    ImportSectionsTableSection importSections = reader.GetImportSectionsTableSection(section);
    Console.WriteLine($"=== ImportSections ({importSections.Entries.Count}) ===");
    int sectionIdx = 0;
    foreach (ImportSectionEntry importSection in importSections.Entries)
    {
        Console.WriteLine($"--- Section [{sectionIdx}] Type={importSection.Type} Flags={importSection.Flags} EntrySize={importSection.EntrySize} Entries={importSection.EntryCount} ---");

        // GC Ref Map — currently disabled due to O(n²) performance in Structural parser
        // TODO: re-enable once GCRefMapTable parsing is optimized
        if ((int)importSection.AuxiliaryDataRva != 0 && importSection.EntryCount > 0)
        {
            Console.WriteLine($"  (GCRefMap aux data present, RVA=0x{(int)importSection.AuxiliaryDataRva:X8} — skipped)");
        }

        // Signatures
        if ((int)importSection.SignatureTableRva == 0 || importSection.EntryCount == 0)
        {
            Console.WriteLine("  (no signatures)");
            sectionIdx++;
            continue;
        }

        int sigTableOffset = reader.GetOffsetForRVA((int)importSection.SignatureTableRva);

        int slotRva = (int)importSection.SectionRva;

        for (int i = 0; i < importSection.EntryCount; i++)
        {
            int currentSlotRva = slotRva + i * importSection.EntrySize;
            int sigPtrOffset = sigTableOffset + i * sizeof(int);
            uint sigRva = nativeReader.ReadUInt32(ref sigPtrOffset);
            if (sigRva == 0)
            {
                Console.WriteLine($"  [{i}] Slot=0x{currentSlotRva:X8} (null signature)");
                continue;
            }
            int sigOffset = reader.GetOffsetForRVA((int)sigRva);

            byte fixupByte = nativeReader.ReadByte(ref sigOffset);
            bool hasModuleOverride = (fixupByte & (byte)ReadyToRunFixupKind.ModuleOverride) != 0;
            ReadyToRunFixupKind fixupKind = (ReadyToRunFixupKind)(fixupByte & ~(byte)ReadyToRunFixupKind.ModuleOverride);

            int moduleIndex = 0;
            string moduleName = resolver.ModuleCount > 0 ? resolver.ModuleNames[0] : "(current)";
            if (hasModuleOverride)
            {
                moduleIndex = (int)nativeReader.ReadCompressedData(ref sigOffset);
                moduleName = moduleIndex < resolver.ModuleCount ? resolver.ModuleNames[moduleIndex] : $"(unknown #{moduleIndex})";
            }

            MetadataReader mdReader = resolver.GetMetadataReader(moduleIndex);
            string target = DecodeSignatureTarget(nativeReader, sigOffset, fixupKind, mdReader, resolver);

            // DEBUG: hex dump for OOB entries
            if (target.Contains("OOB"))
            {
                int debugRawOffset = reader.GetOffsetForRVA((int)sigRva);
                var hexBytes = new System.Text.StringBuilder("  DEBUG hex: ");
                for (int h = 0; h < 30; h++)
                {
                    int tempOff = debugRawOffset + h;
                    hexBytes.Append($"{nativeReader.ReadByte(ref tempOff):X2} ");
                }
                Console.WriteLine(hexBytes.ToString());
                Console.WriteLine($"  DEBUG moduleIndex={moduleIndex} mdReader.MemberRef={mdReader?.GetTableRowCount(TableIndex.MemberRef)} mdReader.MethodDef={mdReader?.GetTableRowCount(TableIndex.MethodDef)}");
            }

            Console.WriteLine($"  [{i}] Slot=0x{currentSlotRva:X8} Fixup={fixupKind,-30} Module={moduleName,-40} Target={target}");
        }
        sectionIdx++;
    }
    Console.WriteLine();
}

static void DumpExceptionInfo(ReadyToRunSectionHandle section, StructuralReader reader)
{
    ExceptionInfoTable table = reader.GetExceptionInfoTable(section);
    Console.WriteLine($"=== ExceptionInfo ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (ExceptionInfoEntry entry in table.Entries)
    {
        Console.WriteLine($"  [{idx,4}] MethodRVA=0x{(int)entry.MethodRva:X8} EhInfoRVA=0x{(int)entry.EhInfoRva:X8}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpDebugInfo(ReadyToRunSectionHandle section, StructuralReader reader)
{
    DebugInfoTable table = reader.GetDebugInfoTable(section);
    Console.WriteLine($"=== DebugInfo ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (DebugInfoEntry entry in table.Entries)
    {
        Console.WriteLine($"  [{idx,4}] RF={entry.RuntimeFunctionIndex,-6} DebugInfoOffset=0x{(int)entry.DebugInfoOffset:X8}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpAvailableTypes(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver,
    MetadataReader mdReaderOverride = null,
    string headerPrefix = null)
{
    AvailableTypesTable table = reader.GetAvailableTypesTable(section);
    MetadataReader mdReader = mdReaderOverride ?? resolver.GetMetadataReader(0);
    string prefix = headerPrefix is not null ? $"{headerPrefix} " : "";
    Console.WriteLine($"=== {prefix}AvailableTypes ({table.Entries.Count}) ===");
    foreach (AvailableTypeEntry entry in table.Entries)
    {
        string kind = entry.IsExportedType ? "ExportedType" : "TypeDef";
        string name;
        if (mdReader is not null && !entry.IsExportedType)
        {
            name = SafeFormatTypeDef(mdReader, (int)entry.Rid);
        }
        else
        {
            name = $"#{entry.Rid}";
        }
        Console.WriteLine($"  {kind} RID={entry.Rid,-6} {name}");
    }
    Console.WriteLine();
}

static void DumpManifestMetadata(ReadyToRunSectionHandle section, StructuralReader reader)
{
    MetadataReader manifest = reader.GetManifestMetadataReader(section);
    int assemblyRefCount = manifest.GetTableRowCount(TableIndex.AssemblyRef);
    Console.WriteLine($"=== ManifestMetadata ({assemblyRefCount} AssemblyRefs) ===");
    for (int i = 1; i <= assemblyRefCount; i++)
    {
        var handle = MetadataTokens.AssemblyReferenceHandle(i);
        var asmRef = manifest.GetAssemblyReference(handle);
        string name = manifest.GetString(asmRef.Name);
        Console.WriteLine($"  [{i}] {name} v{asmRef.Version}");
    }
    Console.WriteLine();
}

static void DumpInliningInfo(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver)
{
    InliningInfoTable table = reader.GetInliningInfoTable(section);
    MetadataReader mdReader = resolver.GetMetadataReader(0);
    Console.WriteLine($"=== InliningInfo ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (InliningInfoEntry entry in table.Entries)
    {
        string inlineeName = SafeFormatMethodDef(mdReader, entry.InlineeRid);
        var inlinerNames = new List<string>(entry.InlinerRids.Count);
        foreach (int rid in entry.InlinerRids)
            inlinerNames.Add(SafeFormatMethodDef(mdReader, rid));
        Console.WriteLine($"  [{idx}] Inlinee RID={entry.InlineeRid} {inlineeName}");
        foreach (string inliner in inlinerNames)
            Console.WriteLine($"         <- {inliner}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpInliningInfo2(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver)
{
    InliningInfo2Table table = reader.GetInliningInfo2Table(section);
    Console.WriteLine($"=== InliningInfo2 ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (InliningInfo2Entry entry in table.Entries)
    {
        MetadataReader inlineeMd = entry.InlineeHasModule
            ? resolver.GetMetadataReader((int)entry.InlineeModuleIndex)
            : resolver.GetMetadataReader(0);
        string inlineeName = SafeFormatMethodDef(inlineeMd, entry.InlineeRid);
        string inlineeModule = entry.InlineeHasModule ? $" Module={entry.InlineeModuleIndex}" : "";
        Console.WriteLine($"  [{idx}] Inlinee RID={entry.InlineeRid}{inlineeModule} {inlineeName}");
        foreach (InlinerRef inliner in entry.Inliners)
        {
            MetadataReader inlinerMd = inliner.HasModule
                ? resolver.GetMetadataReader((int)inliner.ModuleIndex)
                : resolver.GetMetadataReader(0);
            string inlinerName = SafeFormatMethodDef(inlinerMd, inliner.Rid);
            string inlinerModule = inliner.HasModule ? $" Module={inliner.ModuleIndex}" : "";
            Console.WriteLine($"         <- RID={inliner.Rid}{inlinerModule} {inlinerName}");
        }
        idx++;
    }
    Console.WriteLine();
}

static void DumpCrossModuleInlineInfo(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver)
{
    CrossModuleInlineInfoTable table = reader.GetCrossModuleInlineInfoTable(section);
    Console.WriteLine($"=== CrossModuleInlineInfo ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (CrossModuleInlineEntry entry in table.Entries)
    {
        string crossStr = entry.IsCrossModuleInlinee ? " [cross-module]" : "";
        MetadataReader inlineeMd = entry.IsCrossModuleInlinee
            ? resolver.GetMetadataReader((int)entry.InlineeModuleIndex)
            : resolver.GetMetadataReader(0);
        string inlineeName = SafeFormatMethodDef(inlineeMd, (int)entry.InlineeIndex);
        Console.WriteLine($"  [{idx}] Inlinee={entry.InlineeIndex} Module={entry.InlineeModuleIndex}{crossStr} {inlineeName}");
        foreach (CrossModuleInlinerRef inliner in entry.Inliners)
        {
            string inlinerCross = inliner.IsCrossModule ? " [cross-module]" : "";
            MetadataReader inlinerMd = inliner.IsCrossModule
                ? resolver.GetMetadataReader((int)inliner.ModuleIndex)
                : resolver.GetMetadataReader(0);
            string inlinerName = SafeFormatMethodDef(inlinerMd, (int)inliner.Index);
            Console.WriteLine($"         <- Index={inliner.Index} Module={inliner.ModuleIndex}{inlinerCross} {inlinerName}");
        }
        idx++;
    }
    Console.WriteLine();
}

static void DumpComponentAssemblies(ReadyToRunSectionHandle section, StructuralReader reader)
{
    ComponentAssembliesTable table = reader.GetComponentAssembliesTable(section);
    Console.WriteLine($"=== ComponentAssemblies ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (ComponentAssemblyEntry entry in table.Entries)
    {
        Console.WriteLine($"  [{idx}] CorHeaderRVA=0x{entry.CorHeaderRva:X8} Size={entry.CorHeaderSize} AsmHeaderRVA=0x{entry.AssemblyHeaderRva:X8} Size={entry.AssemblyHeaderSize}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpPerComponentSections(
    StructuralReader reader,
    DiskAssemblyResolver resolver)
{
    ReadyToRunSectionHandle? componentAssembliesHandle = null;
    foreach (var s in reader.GetSections())
    {
        if (s.Type == ReadyToRunSectionType.ComponentAssemblies)
        {
            componentAssembliesHandle = s;
            break;
        }
    }

    if (componentAssembliesHandle is null)
        return;

    ComponentAssembliesTable componentTable = reader.GetComponentAssembliesTable(componentAssembliesHandle.Value);

    Console.WriteLine($"=== Per-Component Assembly Sections ({componentTable.Entries.Count} components) ===");
    Console.WriteLine();

    for (int componentIdx = 0; componentIdx < componentTable.Entries.Count; componentIdx++)
    {
        ComponentAssemblyEntry entry = componentTable.Entries[componentIdx];
        if (entry.AssemblyHeaderRva == 0 || entry.AssemblyHeaderSize == 0)
            continue;

        int moduleIndex = componentIdx + resolver.ComponentAssemblyIndexOffset;
        string moduleName = moduleIndex < resolver.ModuleCount
            ? resolver.ModuleNames[moduleIndex]
            : $"(unknown component #{componentIdx})";

        MetadataReader componentMdReader = resolver.GetMetadataReader(moduleIndex);

        int headerOffset = reader.GetOffsetForRVA(entry.AssemblyHeaderRva);
        ReadyToRunCoreHeader coreHeader;
        try
        {
            coreHeader = reader.ReadReadyToRunCoreHeader(ref headerOffset);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"--- Component [{componentIdx}] {moduleName} ---");
            Console.WriteLine($"  (error reading core header: {ex.Message})");
            Console.WriteLine();
            continue;
        }

        string headerPrefix = $"[{moduleName}]";
        Console.WriteLine($"--- Component [{componentIdx}] {moduleName} ({coreHeader.Sections.Count} sections) ---");

        foreach (ReadyToRunSectionHandle section in coreHeader.Sections)
        {
            try
            {
                switch (section.Type)
                {
                    case ReadyToRunSectionType.MethodDefEntryPoints:
                        DumpMethodDefEntryPoints(section, reader, resolver, componentMdReader, headerPrefix);
                        break;
                    case ReadyToRunSectionType.AvailableTypes:
                        DumpAvailableTypes(section, reader, resolver, componentMdReader, headerPrefix);
                        break;
                    case ReadyToRunSectionType.MethodIsGenericMap:
                        DumpMethodIsGenericMap(section, reader, resolver, componentMdReader, headerPrefix);
                        break;
                    case ReadyToRunSectionType.EnclosingTypeMap:
                        DumpEnclosingTypeMap(section, reader, resolver, componentMdReader, headerPrefix);
                        break;
                    case ReadyToRunSectionType.TypeGenericInfoMap:
                        DumpTypeGenericInfoMap(section, reader, resolver, componentMdReader, headerPrefix);
                        break;
                    default:
                        Console.WriteLine($"=== {headerPrefix} {section.Type} (RVA=0x{(int)section.RelativeVirtualAddress:X}, Size={section.Size}) ===");
                        Console.WriteLine($"  (per-component section, not decoded)");
                        Console.WriteLine();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== {headerPrefix} {section.Type} (error: {ex.Message}) ===");
                Console.WriteLine();
            }
        }
    }
}

static void DumpHotColdMap(ReadyToRunSectionHandle section, StructuralReader reader)
{
    HotColdMapTable table = reader.GetHotColdMapTable(section);
    Console.WriteLine($"=== HotColdMap ({table.Entries.Count}) ===");
    foreach (HotColdMapEntry entry in table.Entries)
    {
        Console.WriteLine($"  Hot={entry.HotRuntimeFunctionIndex,-6} Cold={entry.ColdRuntimeFunctionIndex}");
    }
    Console.WriteLine();
}

static void DumpPgoInstrumentationData(ReadyToRunSectionHandle section, StructuralReader reader)
{
    PgoInstrumentationDataTable table = reader.GetPgoInstrumentationDataTable(section);
    Console.WriteLine($"=== PgoInstrumentationData ({table.Entries.Count}) ===");
    int idx = 0;
    foreach (PgoEntry entry in table.Entries)
    {
        Console.WriteLine($"  [{idx,4}] SigOffset=0x{entry.SignatureBlobOffset:X8} Hash=0x{entry.LowHashcode:X8}");
        idx++;
    }
    Console.WriteLine();
}

static void DumpMethodIsGenericMap(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver,
    MetadataReader mdReaderOverride = null,
    string headerPrefix = null)
{
    MethodIsGenericMapTable table = reader.GetMethodIsGenericMapTable(section);
    MetadataReader mdReader = mdReaderOverride ?? resolver.GetMetadataReader(0);
    string prefix = headerPrefix is not null ? $"{headerPrefix} " : "";
    Console.WriteLine($"=== {prefix}MethodIsGenericMap ({table.Count} entries) ===");
    for (int rid = 1; rid <= table.Count; rid++)
    {
        bool isGeneric = table.IsGeneric(rid);
        if (isGeneric)
        {
            string name = SafeFormatMethodDef(mdReader, rid);
            Console.WriteLine($"  RID={rid,-6} IsGeneric=true  {name}");
        }
    }
    Console.WriteLine();
}

static void DumpEnclosingTypeMap(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver,
    MetadataReader mdReaderOverride = null,
    string headerPrefix = null)
{
    EnclosingTypeMapTable table = reader.GetEnclosingTypeMapTable(section);
    MetadataReader mdReader = mdReaderOverride ?? resolver.GetMetadataReader(0);
    string prefix = headerPrefix is not null ? $"{headerPrefix} " : "";
    Console.WriteLine($"=== {prefix}EnclosingTypeMap ({table.Count} entries) ===");
    for (int rid = 1; rid <= table.Count; rid++)
    {
        int enclosingRid = table.GetEnclosingTypeRid(rid);
        if (enclosingRid != 0)
        {
            string name = SafeFormatTypeDef(mdReader, rid);
            string enclosingName = SafeFormatTypeDef(mdReader, enclosingRid);
            Console.WriteLine($"  TypeDef RID={rid,-6} {name}  enclosedBy  RID={enclosingRid,-6} {enclosingName}");
        }
    }
    Console.WriteLine();
}

static void DumpTypeGenericInfoMap(
    ReadyToRunSectionHandle section,
    StructuralReader reader,
    DiskAssemblyResolver resolver,
    MetadataReader mdReaderOverride = null,
    string headerPrefix = null)
{
    TypeGenericInfoMapTable table = reader.GetTypeGenericInfoMapTable(section);
    MetadataReader mdReader = mdReaderOverride ?? resolver.GetMetadataReader(0);
    string prefix = headerPrefix is not null ? $"{headerPrefix} " : "";
    Console.WriteLine($"=== {prefix}TypeGenericInfoMap ({table.Count} entries) ===");
    for (int rid = 1; rid <= table.Count; rid++)
    {
        ReadyToRunTypeGenericInfo info = table.GetInfo(rid);
        if (info != 0)
        {
            int genericCount = (int)(info & ReadyToRunTypeGenericInfo.GenericCountMask);
            bool hasVariance = (info & ReadyToRunTypeGenericInfo.HasVariance) != 0;
            bool hasConstraints = (info & ReadyToRunTypeGenericInfo.HasConstraints) != 0;
            string name = SafeFormatTypeDef(mdReader, rid);
            Console.WriteLine($"  RID={rid,-6} GenericCount={genericCount} HasVariance={hasVariance} HasConstraints={hasConstraints} {name}");
        }
    }
    Console.WriteLine();
}

static void DumpRawSection(ReadyToRunSectionHandle section)
{
    Console.WriteLine($"=== {section.Type} (RVA=0x{(int)section.RelativeVirtualAddress:X}, Size={section.Size}) ===");
    Console.WriteLine("  (no structured dump available)");
    Console.WriteLine();
}

// ─── Signature blob decoding ────────────────────────────────────────────────

static string DecodeSignatureBlobAsMethod(
    NativeReader nativeReader,
    int blobOffset,
    DiskAssemblyResolver resolver)
{
    try
    {
        int offset = blobOffset;
        // InstanceMethodEntryPoints signatures start directly with method flags
        // (no fixup byte prefix). DecodeMethod reads method flags as the first thing.
        MetadataReader mdReader = resolver.GetMetadataReader(0);
        return DecodeMethod(nativeReader, ref offset, mdReader, resolver, peekModuleOverride: true);
    }
    catch (Exception ex)
    {
        return $"(decode error: {ex.Message})";
    }
}

// ─── Signature target decoding ──────────────────────────────────────────────

static string DecodeSignatureTarget(
    NativeReader reader, int offset, ReadyToRunFixupKind fixupKind,
    MetadataReader mdReader, DiskAssemblyResolver resolver)
{
    try
    {
        return fixupKind switch
        {
            ReadyToRunFixupKind.TypeHandle
                or ReadyToRunFixupKind.NewObject
                or ReadyToRunFixupKind.NewArray
                or ReadyToRunFixupKind.IsInstanceOf
                or ReadyToRunFixupKind.TypeDictionary
                or ReadyToRunFixupKind.DeclaringTypeHandle
                or ReadyToRunFixupKind.ChkCast
                => DecodeType(reader, ref offset, mdReader, resolver),

            ReadyToRunFixupKind.MethodHandle
                or ReadyToRunFixupKind.MethodEntry
                or ReadyToRunFixupKind.VirtualEntry
                or ReadyToRunFixupKind.MethodDictionary
                => DecodeMethod(reader, ref offset, mdReader, resolver),

            // DefToken/RefToken fixups encode just a raw RID, not a full method signature
            ReadyToRunFixupKind.MethodEntry_DefToken
                or ReadyToRunFixupKind.VirtualEntry_DefToken
                => DecodeMethodDefToken(reader, ref offset, mdReader),

            ReadyToRunFixupKind.MethodEntry_RefToken
                or ReadyToRunFixupKind.VirtualEntry_RefToken
                => DecodeMethodRefToken(reader, ref offset, mdReader),

            ReadyToRunFixupKind.FieldHandle
                or ReadyToRunFixupKind.FieldAddress
                or ReadyToRunFixupKind.FieldOffset
                => DecodeField(reader, ref offset, mdReader, resolver),

            ReadyToRunFixupKind.CctorTrigger
                or ReadyToRunFixupKind.StaticBaseGC
                or ReadyToRunFixupKind.StaticBaseNonGC
                or ReadyToRunFixupKind.ThreadStaticBaseGC
                or ReadyToRunFixupKind.ThreadStaticBaseNonGC
                => DecodeType(reader, ref offset, mdReader, resolver),

            ReadyToRunFixupKind.Verify_FieldOffset =>
                DecodeVerifyFieldOffset(reader, ref offset, mdReader, resolver),

            ReadyToRunFixupKind.Check_FieldOffset =>
                DecodeCheckFieldOffset(reader, ref offset, mdReader, resolver),

            ReadyToRunFixupKind.StringHandle =>
                DecodeStringHandle(reader, ref offset),

            ReadyToRunFixupKind.Helper =>
                DecodeHelper(reader, ref offset),

            ReadyToRunFixupKind.DelegateCtor =>
                DecodeMethod(reader, ref offset, mdReader, resolver) + " => " + DecodeType(reader, ref offset, mdReader, resolver),

            _ => "(undecoded)"
        };
    }
    catch (Exception ex)
    {
        return $"(decode error: {ex.Message})";
    }
}

static string DecodeVerifyFieldOffset(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver)
{
    uint val1 = ReadCompressedUInt(reader, ref offset);
    uint val2 = ReadCompressedUInt(reader, ref offset);
    string field = DecodeField(reader, ref offset, mdReader, resolver);
    return $"offset={val1} flags=0x{val2:X} {field}";
}

static string DecodeCheckFieldOffset(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver)
{
    uint expectedOffset = ReadCompressedUInt(reader, ref offset);
    string field = DecodeField(reader, ref offset, mdReader, resolver);
    return $"offset={expectedOffset} {field}";
}

static string DecodeMethodDefToken(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint rid = ReadCompressedUInt(reader, ref offset);
    return SafeFormatMethodDef(mdReader, (int)rid);
}

static string DecodeMethodRefToken(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint rid = ReadCompressedUInt(reader, ref offset);
    return SafeFormatMemberRef(mdReader, (int)rid);
}

static string DecodeMethod(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver, bool peekModuleOverride = false)
{
    uint methodFlags = ReadCompressedUInt(reader, ref offset);
    var flags = (ReadyToRunMethodSigFlags)methodFlags;
    MetadataReader outerMdReader = mdReader;

    // UpdateContext: the next uint is a module index that switches the metadata context
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UpdateContext))
    {
        int moduleIndex = (int)ReadCompressedUInt(reader, ref offset);
        MetadataReader contextReader = resolver?.GetMetadataReader(moduleIndex);
        if (contextReader is not null)
        {
            mdReader = contextReader;
            outerMdReader = contextReader;
        }
    }

    string owningType = null;
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType))
    {
        // For InstanceMethodEntryPoints (peekModuleOverride=true), peek for MODULE_ZAPSIG
        // to update mdReader for the method RID lookup. This matches the reference code's
        // GetMetadataReaderFromModuleOverride() in DecodeMethodSignature.
        // For import fixups (peekModuleOverride=false), don't peek — the fixup-level module
        // override already set the correct mdReader, and MODULE_ZAPSIG only affects type
        // decoding internally.
        if (peekModuleOverride && !flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UpdateContext))
        {
            int peekOffset = offset;
            byte peekByte = reader.ReadByte(ref peekOffset);
            if ((CorElementType)peekByte == CorElementType.ELEMENT_TYPE_MODULE_ZAPSIG)
            {
                int moduleIndex = (int)ReadCompressedUInt(reader, ref peekOffset);
                MetadataReader moduleReader = resolver?.GetMetadataReader(moduleIndex);
                if (moduleReader is not null)
                    mdReader = moduleReader;
            }
        }
        owningType = DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
    }

    uint rid = ReadCompressedUInt(reader, ref offset);

    string methodName;
    bool isMemberRef = flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MemberRefToken);
    if (owningType is not null)
    {
        // OwnerType already provides the fully-qualified type (with generic args).
        // Just append the simple method name to avoid duplicating the type path.
        string simpleName = isMemberRef
            ? FormatMemberRefSimpleName(mdReader, (int)rid)
            : FormatMethodDefSimpleName(mdReader, (int)rid);
        methodName = $"{owningType}.{simpleName}";
    }
    else if (mdReader is not null)
    {
        methodName = isMemberRef
            ? FormatMemberRef(mdReader, (int)rid)
            : FormatMethodDef(mdReader, (int)rid);
    }
    else
    {
        methodName = isMemberRef ? $"MemberRef#{rid}" : $"MethodDef#{rid}";
    }

    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation))
    {
        uint typeArgCount = ReadCompressedUInt(reader, ref offset);
        var args = new string[(int)typeArgCount];
        for (int j = 0; j < (int)typeArgCount; j++)
            args[j] = DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
        methodName += $"<{string.Join(", ", args)}>";
    }

    // Constrained: consume the constraining type
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained))
    {
        string constrainedType = DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
        methodName += $" constrained({constrainedType})";
    }

    var extra = new List<string>();
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub)) extra.Add("unbox");
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub)) extra.Add("inst");
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant)) extra.Add("ASYNC");
    if (extra.Count > 0)
        methodName += $" [{string.Join(",", extra)}]";

    return methodName;
}

static string DecodeField(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver)
{
    uint fieldFlags = ReadCompressedUInt(reader, ref offset);
    MetadataReader outerMdReader = mdReader;

    string owningType = null;
    if ((fieldFlags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_OwnerType) != 0)
    {
        // MODULE_ZAPSIG inside the owning type is handled by DecodeType internally.
        // For import fixups, the fixup-level module override already set the correct mdReader.
        owningType = DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
    }

    uint rid = ReadCompressedUInt(reader, ref offset);
    bool isMemberRef = (fieldFlags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_MemberRefToken) != 0;

    string fieldName;
    if (owningType is not null)
    {
        string simpleName = isMemberRef
            ? FormatMemberRefSimpleName(mdReader, (int)rid)
            : FormatFieldDefSimpleName(mdReader, (int)rid);
        fieldName = $"{owningType}.{simpleName}";
    }
    else if (mdReader is not null)
    {
        fieldName = isMemberRef
            ? FormatMemberRef(mdReader, (int)rid)
            : FormatFieldDef(mdReader, (int)rid);
    }
    else
    {
        fieldName = isMemberRef ? $"MemberRef#{rid}" : $"FieldDef#{rid}";
    }

    return fieldName;
}

static string DecodeStringHandle(NativeReader reader, ref int offset)
{
    uint rid = ReadCompressedUInt(reader, ref offset);
    return $"UserString#0x{rid:X}";
}

static string DecodeHelper(NativeReader reader, ref int offset)
{
    uint helperId = ReadCompressedUInt(reader, ref offset);
    return $"Helper({(ReadyToRunHelper)helperId})";
}

// ─── Type signature decoding ────────────────────────────────────────────────

static string DecodeType(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver, MetadataReader outerMdReader = null)
{
    outerMdReader ??= mdReader;
    byte elemType = reader.ReadByte(ref offset);
    return (CorElementType)elemType switch
    {
        CorElementType.ELEMENT_TYPE_VOID => "void",
        CorElementType.ELEMENT_TYPE_BOOLEAN => "bool",
        CorElementType.ELEMENT_TYPE_CHAR => "char",
        CorElementType.ELEMENT_TYPE_I1 => "sbyte",
        CorElementType.ELEMENT_TYPE_U1 => "byte",
        CorElementType.ELEMENT_TYPE_I2 => "short",
        CorElementType.ELEMENT_TYPE_U2 => "ushort",
        CorElementType.ELEMENT_TYPE_I4 => "int",
        CorElementType.ELEMENT_TYPE_U4 => "uint",
        CorElementType.ELEMENT_TYPE_I8 => "long",
        CorElementType.ELEMENT_TYPE_U8 => "ulong",
        CorElementType.ELEMENT_TYPE_R4 => "float",
        CorElementType.ELEMENT_TYPE_R8 => "double",
        CorElementType.ELEMENT_TYPE_STRING => "string",
        CorElementType.ELEMENT_TYPE_OBJECT => "object",
        CorElementType.ELEMENT_TYPE_I => "nint",
        CorElementType.ELEMENT_TYPE_U => "nuint",
        CorElementType.ELEMENT_TYPE_TYPEDBYREF => "TypedReference",

        CorElementType.ELEMENT_TYPE_PTR => DecodeType(reader, ref offset, mdReader, resolver, outerMdReader) + "*",
        CorElementType.ELEMENT_TYPE_BYREF => "ref " + DecodeType(reader, ref offset, mdReader, resolver, outerMdReader),

        CorElementType.ELEMENT_TYPE_VALUETYPE or CorElementType.ELEMENT_TYPE_CLASS =>
            DecodeTypeToken(reader, ref offset, mdReader),

        CorElementType.ELEMENT_TYPE_VAR =>
            $"!{ReadCompressedUInt(reader, ref offset)}",

        CorElementType.ELEMENT_TYPE_MVAR =>
            $"!!{ReadCompressedUInt(reader, ref offset)}",

        CorElementType.ELEMENT_TYPE_SZARRAY =>
            DecodeType(reader, ref offset, mdReader, resolver, outerMdReader) + "[]",

        CorElementType.ELEMENT_TYPE_ARRAY =>
            DecodeArrayType(reader, ref offset, mdReader, resolver, outerMdReader),

        CorElementType.ELEMENT_TYPE_GENERICINST =>
            DecodeGenericInst(reader, ref offset, mdReader, resolver, outerMdReader),

        CorElementType.ELEMENT_TYPE_FNPTR =>
            "fnptr",

        CorElementType.ELEMENT_TYPE_CANON_ZAPSIG =>
            "__Canon",

        CorElementType.ELEMENT_TYPE_MODULE_ZAPSIG =>
            DecodeModuleZapSigType(reader, ref offset, mdReader, resolver, outerMdReader),

        _ => $"(elemType=0x{elemType:X2})"
    };
}

static string DecodeTypeToken(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint encoded = ReadCompressedUInt(reader, ref offset);
    int rid = (int)(encoded >> 2);
    int tableIndex = (int)(encoded & 3);

    if (mdReader is null || rid == 0)
    {
        string tableName = tableIndex switch { 0 => "TypeDef", 1 => "TypeRef", 2 => "TypeSpec", _ => "?" };
        return $"{tableName}#{rid}";
    }

    return tableIndex switch
    {
        0 => rid <= mdReader.GetTableRowCount(TableIndex.TypeDef)
            ? FormatTypeDef(mdReader, rid)
            : $"TypeDef#{rid}(OOB:{mdReader.GetTableRowCount(TableIndex.TypeDef)})",
        1 => rid <= mdReader.GetTableRowCount(TableIndex.TypeRef)
            ? FormatTypeRef(mdReader, rid)
            : $"TypeRef#{rid}(OOB:{mdReader.GetTableRowCount(TableIndex.TypeRef)})",
        2 => $"TypeSpec#{rid}",
        _ => $"?#{rid}"
    };
}

static string DecodeModuleZapSigType(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver, MetadataReader outerMdReader)
{
    int moduleIndex = (int)ReadCompressedUInt(reader, ref offset);
    MetadataReader moduleReader = resolver?.GetMetadataReader(moduleIndex);
    if (moduleReader is not null)
        mdReader = moduleReader;
    // outerMdReader is preserved — MODULE_ZAPSIG only changes the current context,
    // not the outer context used for GENERICINST type arguments.
    return DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
}

static string DecodeGenericInst(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver, MetadataReader outerMdReader)
{
    reader.ReadByte(ref offset);
    string openType = DecodeTypeToken(reader, ref offset, mdReader);
    uint argCount = ReadCompressedUInt(reader, ref offset);
    var args = new string[(int)argCount];
    // Type arguments use the outer reader (fixup-level), matching the reference code's
    // outerDecoder pattern where _outerReader is used instead of _metadataReader.
    for (int i = 0; i < (int)argCount; i++)
        args[i] = DecodeType(reader, ref offset, outerMdReader, resolver, outerMdReader);
    return $"{openType}<{string.Join(",", args)}>";
}

static string DecodeArrayType(NativeReader reader, ref int offset, MetadataReader mdReader, DiskAssemblyResolver resolver, MetadataReader outerMdReader)
{
    string elemType = DecodeType(reader, ref offset, mdReader, resolver, outerMdReader);
    uint rank = ReadCompressedUInt(reader, ref offset);
    uint sizeCount = ReadCompressedUInt(reader, ref offset);
    for (uint i = 0; i < sizeCount; i++) ReadCompressedUInt(reader, ref offset);
    uint lbCount = ReadCompressedUInt(reader, ref offset);
    for (uint i = 0; i < lbCount; i++) ReadCompressedUInt(reader, ref offset);
    return $"{elemType}[{new string(',', (int)rank - 1)}]";
}

// ─── Metadata name resolution ───────────────────────────────────────────────

static string SafeFormatMethodDef(MetadataReader mdReader, int rid)
{
    if (mdReader is null)
        return $"MethodDef#{rid}";
    try
    {
        return FormatMethodDef(mdReader, rid);
    }
    catch
    {
        return $"MethodDef#{rid}";
    }
}

static string SafeFormatTypeDef(MetadataReader mdReader, int rid)
{
    if (mdReader is null)
        return $"TypeDef#{rid}";
    try
    {
        return FormatTypeDef(mdReader, rid);
    }
    catch
    {
        return $"TypeDef#{rid}";
    }
}

static string SafeFormatMemberRef(MetadataReader mdReader, int rid)
{
    if (mdReader is null)
        return $"MemberRef#{rid}";
    try
    {
        return FormatMemberRef(mdReader, rid);
    }
    catch
    {
        return $"MemberRef#{rid}";
    }
}

static string FormatTypeDef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.TypeDefinitionHandle(rid);
    var typeDef = mdReader.GetTypeDefinition(handle);
    string name = mdReader.GetString(typeDef.Name);
    var declaringType = typeDef.GetDeclaringType();
    if (!declaringType.IsNil)
    {
        int enclosingRid = MetadataTokens.GetRowNumber(declaringType);
        string enclosing = FormatTypeDef(mdReader, enclosingRid);
        return $"{enclosing}+{name}";
    }
    string ns = mdReader.GetString(typeDef.Namespace);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string FormatTypeRef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.TypeReferenceHandle(rid);
    var typeRef = mdReader.GetTypeReference(handle);
    string name = mdReader.GetString(typeRef.Name);
    var resScope = typeRef.ResolutionScope;
    if (resScope.Kind == HandleKind.TypeReference)
    {
        int enclosingRid = MetadataTokens.GetRowNumber((TypeReferenceHandle)resScope);
        string enclosing = FormatTypeRef(mdReader, enclosingRid);
        return $"{enclosing}+{name}";
    }
    string ns = mdReader.GetString(typeRef.Namespace);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string FormatMethodDef(MetadataReader mdReader, int rid)
{
    if (rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MethodDef))
        return $"MethodDef#{rid}(OOB:{mdReader.GetTableRowCount(TableIndex.MethodDef)})";
    var handle = MetadataTokens.MethodDefinitionHandle(rid);
    var methodDef = mdReader.GetMethodDefinition(handle);
    string name = mdReader.GetString(methodDef.Name);
    var declaringType = methodDef.GetDeclaringType();
    if (!declaringType.IsNil)
    {
        int typeRid = MetadataTokens.GetRowNumber(declaringType);
        string fullType = FormatTypeDef(mdReader, typeRid);
        return $"{fullType}.{name}";
    }
    return name;
}

static string FormatFieldDef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.FieldDefinitionHandle(rid);
    var fieldDef = mdReader.GetFieldDefinition(handle);
    string name = mdReader.GetString(fieldDef.Name);
    var declaringType = fieldDef.GetDeclaringType();
    if (!declaringType.IsNil)
    {
        int typeRid = MetadataTokens.GetRowNumber(declaringType);
        string fullType = FormatTypeDef(mdReader, typeRid);
        return $"{fullType}.{name}";
    }
    return name;
}

static string FormatMemberRef(MetadataReader mdReader, int rid)
{
    if (rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MemberRef))
        return $"MemberRef#{rid}(OOB:{mdReader.GetTableRowCount(TableIndex.MemberRef)})";
    var handle = MetadataTokens.MemberReferenceHandle(rid);
    var memberRef = mdReader.GetMemberReference(handle);
    string name = mdReader.GetString(memberRef.Name);
    var parent = memberRef.Parent;
    if (parent.Kind == HandleKind.TypeReference)
    {
        int typeRid = MetadataTokens.GetRowNumber((TypeReferenceHandle)parent);
        return $"{FormatTypeRef(mdReader, typeRid)}.{name}";
    }
    if (parent.Kind == HandleKind.TypeDefinition)
    {
        int typeRid = MetadataTokens.GetRowNumber((TypeDefinitionHandle)parent);
        return $"{FormatTypeDef(mdReader, typeRid)}.{name}";
    }
    return name;
}

static string FormatMethodDefSimpleName(MetadataReader mdReader, int rid)
{
    if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MethodDef))
        return $"MethodDef#{rid}";
    var handle = MetadataTokens.MethodDefinitionHandle(rid);
    var methodDef = mdReader.GetMethodDefinition(handle);
    return mdReader.GetString(methodDef.Name);
}

static string FormatMemberRefSimpleName(MetadataReader mdReader, int rid)
{
    if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MemberRef))
        return $"MemberRef#{rid}";
    var handle = MetadataTokens.MemberReferenceHandle(rid);
    var memberRef = mdReader.GetMemberReference(handle);
    return mdReader.GetString(memberRef.Name);
}

static string FormatFieldDefSimpleName(MetadataReader mdReader, int rid)
{
    if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.Field))
        return $"FieldDef#{rid}";
    var handle = MetadataTokens.FieldDefinitionHandle(rid);
    var fieldDef = mdReader.GetFieldDefinition(handle);
    return mdReader.GetString(fieldDef.Name);
}

// ─── Compressed uint helper (ECMA-335 encoding) ────────────────────────────

static uint ReadCompressedUInt(NativeReader reader, ref int offset)
{
    byte first = reader.ReadByte(ref offset);
    if ((first & 0x80) == 0)
        return first;
    if ((first & 0xC0) == 0x80)
    {
        uint res = (uint)(first & 0x3F) << 8;
        res |= reader.ReadByte(ref offset);
        return res;
    }
    uint val = (uint)(first & 0x1F) << 24;
    val |= (uint)reader.ReadByte(ref offset) << 16;
    val |= (uint)reader.ReadByte(ref offset) << 8;
    val |= reader.ReadByte(ref offset);
    return val;
}
