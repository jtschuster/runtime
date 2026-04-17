// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

using StructuralReader = ILCompiler.Reflection.ReadyToRun.Structural.ReadyToRunReader;

namespace ILCompiler.Reflection.ReadyToRun.Structural.Assertions;

/// <summary>
/// An inventory of all methods compiled into an R2R image. Built lazily from the
/// Structural reader and the assembly resolver. Each entry carries a human-readable
/// signature string (with <c>[ASYNC]</c>/<c>[RESUME]</c> prefixes when applicable),
/// the owning module index, and the set of fixup cells that apply to the method.
/// </summary>
internal sealed class MethodInventory
{
    public IReadOnlyList<MethodInventoryEntry> Methods { get; }

    /// <summary>All decoded fixup signatures in the image, in slot order per section.</summary>
    public IReadOnlyList<ImportSectionSignatures> ImportSections { get; }

    private MethodInventory(
        IReadOnlyList<MethodInventoryEntry> methods,
        IReadOnlyList<ImportSectionSignatures> importSections)
    {
        Methods = methods;
        ImportSections = importSections;
    }

    public static MethodInventory Build(StructuralReader reader, NativeReader nativeReader, AssertionsAssemblyResolver resolver)
    {
        var importSections = BuildImportSectionSignatures(reader, nativeReader);
        var methods = new List<MethodInventoryEntry>();

        // Walk image-wide sections
        bool foundImageWideMethodDef = false;
        foreach (ReadyToRunSectionHandle section in reader.GetSections())
        {
            switch (section.Type)
            {
                case ReadyToRunSectionType.MethodDefEntryPoints:
                    // In non-composite images this contains the methods from module 0.
                    // In composite images this section is absent at image scope; per-component
                    // sections carry method def entry points instead.
                    AddMethodDefEntries(reader, resolver, section, moduleIndex: 0, importSections, methods);
                    foundImageWideMethodDef = true;
                    break;
                case ReadyToRunSectionType.InstanceMethodEntryPoints:
                    AddInstanceMethodEntries(reader, nativeReader, resolver, section, importSections, methods);
                    break;
            }
        }

        // Composite: walk per-component MethodDefEntryPoints
        if (reader.Composite && !foundImageWideMethodDef)
        {
            AddComponentMethodDefEntries(reader, resolver, importSections, methods);
        }

        // Synthesize [RESUME] entries from ResumptionStubEntryPoint fixups.
        AddResumeEntries(methods);

        return new MethodInventory(methods, importSections);
    }

    // ─── Import section signatures ──────────────────────────────────────────

    private static List<ImportSectionSignatures> BuildImportSectionSignatures(StructuralReader reader, NativeReader nativeReader)
    {
        var sections = new List<ImportSectionSignatures>();
        foreach (ReadyToRunSectionHandle sectionHandle in reader.GetSections())
        {
            if (sectionHandle.Type != ReadyToRunSectionType.ImportSections)
                continue;

            ImportSectionsTableSection table = reader.GetImportSectionsTableSection(sectionHandle);
            uint tableIndex = 0;
            foreach (ImportSectionEntry entry in table.Entries)
            {
                var sigs = new R2RFixupSignature[entry.EntryCount];
                if ((int)entry.SignatureTableRva != 0 && entry.EntryCount > 0)
                {
                    int sigTableOffset = reader.GetOffsetForRVA((int)entry.SignatureTableRva);
                    for (int i = 0; i < entry.EntryCount; i++)
                    {
                        int sigPtrOffset = sigTableOffset + i * sizeof(int);
                        uint sigRva = nativeReader.ReadUInt32(ref sigPtrOffset);
                        if (sigRva == 0)
                            continue;
                        try
                        {
                            sigs[i] = reader.DecodeFixupSignature((int)sigRva);
                        }
                        catch
                        {
                            sigs[i] = null;
                        }
                    }
                }
                sections.Add(new ImportSectionSignatures(tableIndex++, entry, sigs));
            }
        }
        return sections;
    }

    // ─── MethodDef entry points (per-component or image-wide) ──────────────

    private static void AddMethodDefEntries(
        StructuralReader reader,
        AssertionsAssemblyResolver resolver,
        ReadyToRunSectionHandle section,
        int moduleIndex,
        IReadOnlyList<ImportSectionSignatures> importSections,
        List<MethodInventoryEntry> output)
    {
        MethodDefEntryPointsTable table = reader.GetMethodDefEntryPointsTable(section);
        MetadataReader mdReader = resolver.GetMetadataReader(moduleIndex);

        foreach (MethodDefEntry entry in table.Entries)
        {
            if (entry is null)
                continue;

            string name = SafeFormatMethodDef(mdReader, (int)entry.Rid);
            bool isAsync = TryDetectAsyncAttribute(mdReader, (int)entry.Rid);

            var fixups = ResolveFixupCells(entry.FixupCells, importSections);

            output.Add(new MethodInventoryEntry
            {
                Signature = (isAsync ? "[ASYNC] " : "") + name,
                SimpleName = name,
                ModuleIndex = moduleIndex,
                Rid = entry.Rid,
                IsAsync = isAsync,
                IsResumeStub = false,
                IsMemberRef = false,
                EntryPointIndex = (int)entry.EntryPointIndex,
                Fixups = fixups,
            });
        }
    }

    private static void AddComponentMethodDefEntries(
        StructuralReader reader,
        AssertionsAssemblyResolver resolver,
        IReadOnlyList<ImportSectionSignatures> importSections,
        List<MethodInventoryEntry> output)
    {
        ReadyToRunSectionHandle? componentAssembliesHandle = null;
        foreach (ReadyToRunSectionHandle s in reader.GetSections())
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
        for (int componentIdx = 0; componentIdx < componentTable.Entries.Count; componentIdx++)
        {
            ComponentAssemblyEntry entry = componentTable.Entries[componentIdx];
            if (entry.AssemblyHeaderRva == 0 || entry.AssemblyHeaderSize == 0)
                continue;

            int moduleIndex = componentIdx + resolver.ComponentAssemblyIndexOffset;

            int headerOffset = reader.GetOffsetForRVA(entry.AssemblyHeaderRva);
            ReadyToRunCoreHeader coreHeader;
            try
            {
                coreHeader = reader.ReadReadyToRunCoreHeader(ref headerOffset);
            }
            catch
            {
                continue;
            }

            foreach (ReadyToRunSectionHandle section in coreHeader.Sections)
            {
                if (section.Type == ReadyToRunSectionType.MethodDefEntryPoints)
                {
                    AddMethodDefEntries(reader, resolver, section, moduleIndex, importSections, output);
                }
            }
        }
    }

    // ─── Instance method entry points ──────────────────────────────────────

    private static void AddInstanceMethodEntries(
        StructuralReader reader,
        NativeReader nativeReader,
        AssertionsAssemblyResolver resolver,
        ReadyToRunSectionHandle section,
        IReadOnlyList<ImportSectionSignatures> importSections,
        List<MethodInventoryEntry> output)
    {
        InstanceMethodEntryPointsTable table = reader.GetInstanceMethodEntryPointsTable(section);

        foreach (InstanceMethodEntry entry in table.Entries)
        {
            InstanceMethodPayload payload;
            try
            {
                payload = reader.GetInstanceMethodPayload(entry);
            }
            catch
            {
                continue;
            }

            MethodSignature methodSig = payload.Method;
            int ownerModule = methodSig.ModuleIndex >= 0 ? methodSig.ModuleIndex : 0;
            string name = FormatMethodFromSignature(methodSig, resolver);
            bool isAsync = methodSig.Flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant);

            var fixups = ResolveFixupCells(payload.FixupCells, importSections);

            output.Add(new MethodInventoryEntry
            {
                Signature = (isAsync ? "[ASYNC] " : "") + name,
                SimpleName = name,
                ModuleIndex = ownerModule,
                Rid = (uint)methodSig.Rid,
                IsAsync = isAsync,
                IsResumeStub = false,
                IsMemberRef = methodSig.IsMemberRef,
                EntryPointIndex = (int)payload.EntryPointIndex,
                Fixups = fixups,
            });
        }
    }

    // ─── Fixup cell resolution ─────────────────────────────────────────────

    private static IReadOnlyList<FixupInfo> ResolveFixupCells(
        IReadOnlyList<FixupCellRef> cellRefs,
        IReadOnlyList<ImportSectionSignatures> importSections)
    {
        if (cellRefs is null || cellRefs.Count == 0)
            return Array.Empty<FixupInfo>();

        var result = new List<FixupInfo>(cellRefs.Count);
        foreach (FixupCellRef cell in cellRefs)
        {
            if (cell.TableIndex >= importSections.Count)
                continue;
            ImportSectionSignatures section = importSections[(int)cell.TableIndex];
            if (cell.CellIndex >= section.Signatures.Length)
                continue;
            R2RFixupSignature sig = section.Signatures[cell.CellIndex];
            result.Add(new FixupInfo
            {
                TableIndex = cell.TableIndex,
                CellIndex = cell.CellIndex,
                Kind = sig?.Kind ?? default,
                Signature = sig,
            });
        }
        return result;
    }

    // ─── [RESUME] synthetic entries ────────────────────────────────────────

    private static void AddResumeEntries(List<MethodInventoryEntry> methods)
    {
        var toAdd = new List<MethodInventoryEntry>();
        foreach (MethodInventoryEntry method in methods)
        {
            foreach (FixupInfo fixup in method.Fixups)
            {
                if (fixup.Kind == ReadyToRunFixupKind.ResumptionStubEntryPoint)
                {
                    toAdd.Add(new MethodInventoryEntry
                    {
                        Signature = "[RESUME] " + method.SimpleName,
                        SimpleName = method.SimpleName,
                        ModuleIndex = method.ModuleIndex,
                        Rid = method.Rid,
                        IsAsync = method.IsAsync,
                        IsResumeStub = true,
                        IsMemberRef = method.IsMemberRef,
                        EntryPointIndex = method.EntryPointIndex,
                        Fixups = Array.Empty<FixupInfo>(),
                    });
                }
            }
        }
        methods.AddRange(toAdd);
    }

    // ─── Name / signature helpers ──────────────────────────────────────────

    internal static string FormatMethodFromSignature(MethodSignature sig, AssertionsAssemblyResolver resolver)
    {
        int moduleIndex = sig.ModuleIndex >= 0 ? sig.ModuleIndex : 0;
        MetadataReader mdReader = resolver.GetMetadataReader(moduleIndex);
        string name = sig.IsMemberRef
            ? SafeFormatMemberRef(mdReader, sig.Rid)
            : SafeFormatMethodDef(mdReader, sig.Rid);

        if (!sig.TypeArguments.IsDefaultOrEmpty)
        {
            var args = new List<string>(sig.TypeArguments.Length);
            foreach (R2RTypeNode ta in sig.TypeArguments)
                args.Add(ta?.ToString() ?? "?");
            name += "<" + string.Join(", ", args) + ">";
        }
        return name;
    }

    internal static string SafeFormatMethodDef(MetadataReader mdReader, int rid)
    {
        if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MethodDef))
            return $"MethodDef#{rid}";
        try
        {
            var handle = MetadataTokens.MethodDefinitionHandle(rid);
            MethodDefinition md = mdReader.GetMethodDefinition(handle);
            string methodName = mdReader.GetString(md.Name);
            TypeDefinitionHandle declaringType = md.GetDeclaringType();
            if (!declaringType.IsNil)
            {
                string typeName = FormatTypeDef(mdReader, MetadataTokens.GetRowNumber(declaringType));
                return $"{typeName}.{methodName}";
            }
            return methodName;
        }
        catch
        {
            return $"MethodDef#{rid}";
        }
    }

    private static string SafeFormatMemberRef(MetadataReader mdReader, int rid)
    {
        if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MemberRef))
            return $"MemberRef#{rid}";
        try
        {
            var handle = MetadataTokens.MemberReferenceHandle(rid);
            MemberReference mr = mdReader.GetMemberReference(handle);
            string memberName = mdReader.GetString(mr.Name);
            EntityHandle parent = mr.Parent;
            string parentName = parent.Kind switch
            {
                HandleKind.TypeReference => FormatTypeRef(mdReader, MetadataTokens.GetRowNumber((TypeReferenceHandle)parent)),
                HandleKind.TypeDefinition => FormatTypeDef(mdReader, MetadataTokens.GetRowNumber((TypeDefinitionHandle)parent)),
                _ => null,
            };
            return parentName is null ? memberName : $"{parentName}.{memberName}";
        }
        catch
        {
            return $"MemberRef#{rid}";
        }
    }

    private static string FormatTypeDef(MetadataReader mdReader, int rid)
    {
        var handle = MetadataTokens.TypeDefinitionHandle(rid);
        TypeDefinition td = mdReader.GetTypeDefinition(handle);
        string name = mdReader.GetString(td.Name);
        TypeDefinitionHandle declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return FormatTypeDef(mdReader, MetadataTokens.GetRowNumber(declaring)) + "+" + name;
        string ns = mdReader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FormatTypeRef(MetadataReader mdReader, int rid)
    {
        var handle = MetadataTokens.TypeReferenceHandle(rid);
        TypeReference tr = mdReader.GetTypeReference(handle);
        string name = mdReader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FormatTypeRef(mdReader, MetadataTokens.GetRowNumber((TypeReferenceHandle)tr.ResolutionScope)) + "+" + name;
        string ns = mdReader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool TryDetectAsyncAttribute(MetadataReader mdReader, int rid)
    {
        if (mdReader is null || rid <= 0 || rid > mdReader.GetTableRowCount(TableIndex.MethodDef))
            return false;
        try
        {
            var handle = MetadataTokens.MethodDefinitionHandle(rid);
            MethodDefinition md = mdReader.GetMethodDefinition(handle);
            foreach (CustomAttributeHandle cah in md.GetCustomAttributes())
            {
                CustomAttribute ca = mdReader.GetCustomAttribute(cah);
                string typeName = GetCustomAttributeTypeName(mdReader, ca);
                if (typeName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                    || typeName == "System.Runtime.CompilerServices.RuntimeAsyncMethodGenerationAttribute")
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static string GetCustomAttributeTypeName(MetadataReader mdReader, CustomAttribute ca)
    {
        EntityHandle ctor = ca.Constructor;
        EntityHandle typeHandle;
        if (ctor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition md = mdReader.GetMethodDefinition((MethodDefinitionHandle)ctor);
            typeHandle = md.GetDeclaringType();
        }
        else if (ctor.Kind == HandleKind.MemberReference)
        {
            MemberReference mr = mdReader.GetMemberReference((MemberReferenceHandle)ctor);
            typeHandle = mr.Parent;
        }
        else
        {
            return null;
        }

        if (typeHandle.Kind == HandleKind.TypeReference)
            return FormatTypeRef(mdReader, MetadataTokens.GetRowNumber((TypeReferenceHandle)typeHandle));
        if (typeHandle.Kind == HandleKind.TypeDefinition)
            return FormatTypeDef(mdReader, MetadataTokens.GetRowNumber((TypeDefinitionHandle)typeHandle));
        return null;
    }
}

internal sealed class MethodInventoryEntry
{
    public string Signature { get; init; }
    public string SimpleName { get; init; }
    public int ModuleIndex { get; init; }
    public uint Rid { get; init; }
    public bool IsAsync { get; init; }
    public bool IsResumeStub { get; init; }
    public bool IsMemberRef { get; init; }
    public int EntryPointIndex { get; init; }
    public IReadOnlyList<FixupInfo> Fixups { get; init; } = Array.Empty<FixupInfo>();
}

internal sealed class FixupInfo
{
    public uint TableIndex { get; init; }
    public uint CellIndex { get; init; }
    public ReadyToRunFixupKind Kind { get; init; }
    public R2RFixupSignature Signature { get; init; }
}

internal sealed class ImportSectionSignatures
{
    public uint TableIndex { get; }
    public ImportSectionEntry Entry { get; }
    public R2RFixupSignature[] Signatures { get; }

    public ImportSectionSignatures(uint tableIndex, ImportSectionEntry entry, R2RFixupSignature[] signatures)
    {
        TableIndex = tableIndex;
        Entry = entry;
        Signatures = signatures;
    }
}
