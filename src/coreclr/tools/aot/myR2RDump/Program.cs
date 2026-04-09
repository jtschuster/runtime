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
byte[] image = File.ReadAllBytes(filename);
PEReader peReader = new PEReader(new MemoryStream(image));
PEImageReader peImageReader = new PEImageReader(peReader);
NativeReader nativeReader = new NativeReader(new MemoryStream(image));
StructuralReader reader = new StructuralReader(peImageReader, nativeReader, image, filename);

Console.WriteLine($"Image: {Path.GetFileName(filename)}");
Console.WriteLine($"Machine: {reader.Machine}  Composite: {reader.Composite}");
Console.WriteLine();

// Build module index → assembly name mapping from ManifestMetadata + main assembly refs
string[] moduleNames = BuildModuleNameTable(reader, peReader, image);

// Build module index → MetadataReader mapping by loading component DLLs from disk
var (moduleMetadata, _moduleKeepAlive) = BuildModuleMetadata(moduleNames, filename, peReader, reader, image);

Console.WriteLine("=== Module Reference Table ===");
for (int i = 0; i < moduleNames.Length; i++)
{
    string hasMd = moduleMetadata.ContainsKey(i) ? " [metadata loaded]" : "";
    Console.WriteLine($"  #:{i} = {moduleNames[i]}{hasMd}");
}
Console.WriteLine();

// Dump imports
ImportSectionsTable importSections = reader.ImportSections;
if (importSections is null)
{
    Console.WriteLine("No import sections found.");
    return 0;
}

Console.WriteLine($"=== Import Sections ({importSections.Entries.Count}) ===");
foreach (ImportSectionEntry section in importSections.Entries)
{
    Console.WriteLine($"--- Section [{section.Index}] Type={section.Type} Flags={section.Flags} EntrySize={section.EntrySize} Entries={section.EntryCount} ---");

    if (section.SignatureRva == 0 || section.EntryCount == 0)
    {
        Console.WriteLine("  (no signatures)");
        continue;
    }

    int sigTableOffset = reader.GetOffset(section.SignatureRva);

    for (int i = 0; i < section.EntryCount; i++)
    {
        int sigPtrOffset = sigTableOffset + i * sizeof(int);
        uint sigRva = nativeReader.ReadUInt32(ref sigPtrOffset);
        if (sigRva == 0)
        {
            Console.WriteLine($"  [{i}] (null signature)");
            continue;
        }
        int sigOffset = reader.GetOffset((int)sigRva);

        // Read fixup kind byte
        byte fixupByte = nativeReader.ReadByte(ref sigOffset);
        bool hasModuleOverride = (fixupByte & (byte)ReadyToRunFixupKind.ModuleOverride) != 0;
        ReadyToRunFixupKind fixupKind = (ReadyToRunFixupKind)(fixupByte & ~(byte)ReadyToRunFixupKind.ModuleOverride);

        int moduleIndex = 0;
        string moduleName = moduleNames.Length > 0 ? moduleNames[0] : "(current)";
        if (hasModuleOverride)
        {
            moduleIndex = (int)nativeReader.ReadCompressedData(ref sigOffset);
            moduleName = moduleIndex < moduleNames.Length ? moduleNames[moduleIndex] : $"(unknown #{moduleIndex})";
        }

        // Decode the signature target (type/method/field name)
        moduleMetadata.TryGetValue(moduleIndex, out MetadataReader mdReader);
        string target = DecodeSignatureTarget(nativeReader, sigOffset, fixupKind, mdReader);

        Console.WriteLine($"  [{i}] Fixup={fixupKind,-30} Module={moduleName,-40} Target={target}");
    }
}

return 0;

// ─── Signature target decoding ──────────────────────────────────────────────

static string DecodeSignatureTarget(NativeReader reader, int offset, ReadyToRunFixupKind fixupKind, MetadataReader mdReader)
{
    try
    {
        return fixupKind switch
        {
            // Type fixups: signature is a type
            ReadyToRunFixupKind.TypeHandle
                or ReadyToRunFixupKind.NewObject
                or ReadyToRunFixupKind.NewArray
                or ReadyToRunFixupKind.IsInstanceOf
                or ReadyToRunFixupKind.TypeDictionary
                or ReadyToRunFixupKind.DeclaringTypeHandle
                => DecodeType(reader, ref offset, mdReader),

            // Method fixups: flags + owning type + method token
            ReadyToRunFixupKind.MethodHandle
                or ReadyToRunFixupKind.MethodEntry
                or ReadyToRunFixupKind.MethodEntry_DefToken
                or ReadyToRunFixupKind.MethodEntry_RefToken
                or ReadyToRunFixupKind.VirtualEntry
                or ReadyToRunFixupKind.VirtualEntry_DefToken
                or ReadyToRunFixupKind.VirtualEntry_RefToken
                or ReadyToRunFixupKind.MethodDictionary
                => DecodeMethod(reader, ref offset, mdReader),

            // Field fixups: field sig
            ReadyToRunFixupKind.FieldHandle
                or ReadyToRunFixupKind.FieldAddress
                or ReadyToRunFixupKind.FieldOffset
                or ReadyToRunFixupKind.CctorTrigger
                or ReadyToRunFixupKind.StaticBaseGC
                or ReadyToRunFixupKind.StaticBaseNonGC
                or ReadyToRunFixupKind.ThreadStaticBaseGC
                or ReadyToRunFixupKind.ThreadStaticBaseNonGC
                => DecodeField(reader, ref offset, mdReader),

            // Verify_FieldOffset: uint (expected offset) + uint (actual offset?) + field sig
            ReadyToRunFixupKind.Verify_FieldOffset =>
                DecodeVerifyFieldOffset(reader, ref offset, mdReader),

            ReadyToRunFixupKind.Check_FieldOffset =>
                DecodeCheckFieldOffset(reader, ref offset, mdReader),

            // String handle: compressed token for user string
            ReadyToRunFixupKind.StringHandle =>
                DecodeStringHandle(reader, ref offset),

            // Helper: helper ID
            ReadyToRunFixupKind.Helper =>
                DecodeHelper(reader, ref offset),

            // Delegate ctor: method + type
            ReadyToRunFixupKind.DelegateCtor =>
                DecodeMethod(reader, ref offset, mdReader) + " => " + DecodeType(reader, ref offset, mdReader),

            _ => "(undecoded)"
        };
    }
    catch (Exception ex)
    {
        return $"(decode error: {ex.Message})";
    }
}

static string DecodeVerifyFieldOffset(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint val1 = ReadCompressedUInt(reader, ref offset);
    uint val2 = ReadCompressedUInt(reader, ref offset);
    string field = DecodeField(reader, ref offset, mdReader);
    return $"offset={val1} flags=0x{val2:X} {field}";
}

static string DecodeCheckFieldOffset(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint expectedOffset = ReadCompressedUInt(reader, ref offset);
    string field = DecodeField(reader, ref offset, mdReader);
    return $"offset={expectedOffset} {field}";
}

static string DecodeMethod(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint methodFlags = ReadCompressedUInt(reader, ref offset);
    var flags = (ReadyToRunMethodSigFlags)methodFlags;

    string owningType = null;
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType))
    {
        owningType = DecodeType(reader, ref offset, mdReader);
    }

    uint rid = ReadCompressedUInt(reader, ref offset);

    string methodName;
    bool isMemberRef = flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MemberRefToken);
    if (mdReader is not null)
    {
        methodName = isMemberRef
            ? FormatMemberRef(mdReader, (int)rid)
            : FormatMethodDef(mdReader, (int)rid);
    }
    else
    {
        methodName = isMemberRef ? $"MemberRef#{rid}" : $"MethodDef#{rid}";
    }

    if (owningType is not null)
        methodName = $"{owningType}::{methodName}";

    // Decode generic instantiation args (just count them)
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation))
    {
        uint typeArgCount = ReadCompressedUInt(reader, ref offset);
        var args = new string[(int)typeArgCount];
        for (int j = 0; j < (int)typeArgCount; j++)
            args[j] = DecodeType(reader, ref offset, mdReader);
        methodName += $"<{string.Join(", ", args)}>";
    }

    // Flags annotations
    var extra = new List<string>();
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub)) extra.Add("unbox");
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub)) extra.Add("inst");
    if (flags.HasFlag(ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained)) extra.Add("constrained");
    if (extra.Count > 0)
        methodName += $" [{string.Join(",", extra)}]";

    return methodName;
}

static string DecodeField(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint fieldFlags = ReadCompressedUInt(reader, ref offset);

    string owningType = null;
    if ((fieldFlags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_OwnerType) != 0)
    {
        owningType = DecodeType(reader, ref offset, mdReader);
    }

    uint rid = ReadCompressedUInt(reader, ref offset);
    bool isMemberRef = (fieldFlags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_MemberRefToken) != 0;

    string fieldName;
    if (mdReader is not null)
    {
        fieldName = isMemberRef
            ? FormatMemberRef(mdReader, (int)rid)
            : FormatFieldDef(mdReader, (int)rid);
    }
    else
    {
        fieldName = isMemberRef ? $"MemberRef#{rid}" : $"FieldDef#{rid}";
    }

    return owningType is not null ? $"{owningType}::{fieldName}" : fieldName;
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

static string DecodeType(NativeReader reader, ref int offset, MetadataReader mdReader)
{
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

        CorElementType.ELEMENT_TYPE_PTR => DecodeType(reader, ref offset, mdReader) + "*",
        CorElementType.ELEMENT_TYPE_BYREF => "ref " + DecodeType(reader, ref offset, mdReader),

        CorElementType.ELEMENT_TYPE_VALUETYPE or CorElementType.ELEMENT_TYPE_CLASS =>
            DecodeTypeToken(reader, ref offset, mdReader),

        CorElementType.ELEMENT_TYPE_VAR =>
            $"!{ReadCompressedUInt(reader, ref offset)}",

        CorElementType.ELEMENT_TYPE_MVAR =>
            $"!!{ReadCompressedUInt(reader, ref offset)}",

        CorElementType.ELEMENT_TYPE_SZARRAY =>
            DecodeType(reader, ref offset, mdReader) + "[]",

        CorElementType.ELEMENT_TYPE_ARRAY =>
            DecodeArrayType(reader, ref offset, mdReader),

        CorElementType.ELEMENT_TYPE_GENERICINST =>
            DecodeGenericInst(reader, ref offset, mdReader),

        CorElementType.ELEMENT_TYPE_FNPTR =>
            "fnptr",

        CorElementType.ELEMENT_TYPE_CANON_ZAPSIG =>
            "__Canon",

        _ => $"(elemType=0x{elemType:X2})"
    };
}

static string DecodeTypeToken(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    uint encoded = ReadCompressedUInt(reader, ref offset);
    int rid = (int)(encoded >> 2);
    int tableIndex = (int)(encoded & 3);

    if (mdReader is null)
    {
        string tableName = tableIndex switch { 0 => "TypeDef", 1 => "TypeRef", 2 => "TypeSpec", _ => "?" };
        return $"{tableName}#{rid}";
    }

    return tableIndex switch
    {
        0 => FormatTypeDef(mdReader, rid),
        1 => FormatTypeRef(mdReader, rid),
        2 => $"TypeSpec#{rid}",
        _ => $"?#{rid}"
    };
}

static string DecodeGenericInst(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    // Skip the CLASS/VALUETYPE byte
    reader.ReadByte(ref offset);
    string openType = DecodeTypeToken(reader, ref offset, mdReader);
    uint argCount = ReadCompressedUInt(reader, ref offset);
    var args = new string[(int)argCount];
    for (int i = 0; i < (int)argCount; i++)
        args[i] = DecodeType(reader, ref offset, mdReader);
    return $"{openType}<{string.Join(", ", args)}>";
}

static string DecodeArrayType(NativeReader reader, ref int offset, MetadataReader mdReader)
{
    string elemType = DecodeType(reader, ref offset, mdReader);
    uint rank = ReadCompressedUInt(reader, ref offset);
    // Skip sizes and lower bounds
    uint sizeCount = ReadCompressedUInt(reader, ref offset);
    for (uint i = 0; i < sizeCount; i++) ReadCompressedUInt(reader, ref offset);
    uint lbCount = ReadCompressedUInt(reader, ref offset);
    for (uint i = 0; i < lbCount; i++) ReadCompressedUInt(reader, ref offset);
    return $"{elemType}[{new string(',', (int)rank - 1)}]";
}

// ─── Metadata name resolution ───────────────────────────────────────────────

static string FormatTypeDef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.TypeDefinitionHandle(rid);
    var typeDef = mdReader.GetTypeDefinition(handle);
    string ns = mdReader.GetString(typeDef.Namespace);
    string name = mdReader.GetString(typeDef.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string FormatTypeRef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.TypeReferenceHandle(rid);
    var typeRef = mdReader.GetTypeReference(handle);
    string ns = mdReader.GetString(typeRef.Namespace);
    string name = mdReader.GetString(typeRef.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string FormatMethodDef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.MethodDefinitionHandle(rid);
    var methodDef = mdReader.GetMethodDefinition(handle);
    string name = mdReader.GetString(methodDef.Name);
    var declaringType = methodDef.GetDeclaringType();
    if (!declaringType.IsNil)
    {
        var typeDef = mdReader.GetTypeDefinition(declaringType);
        string typeName = mdReader.GetString(typeDef.Name);
        string ns = mdReader.GetString(typeDef.Namespace);
        string fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
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
        var typeDef = mdReader.GetTypeDefinition(declaringType);
        string typeName = mdReader.GetString(typeDef.Name);
        string ns = mdReader.GetString(typeDef.Namespace);
        string fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        return $"{fullType}.{name}";
    }
    return name;
}

static string FormatMemberRef(MetadataReader mdReader, int rid)
{
    var handle = MetadataTokens.MemberReferenceHandle(rid);
    var memberRef = mdReader.GetMemberReference(handle);
    string name = mdReader.GetString(memberRef.Name);
    var parent = memberRef.Parent;
    if (parent.Kind == HandleKind.TypeReference)
    {
        var typeRef = mdReader.GetTypeReference((TypeReferenceHandle)parent);
        string typeName = mdReader.GetString(typeRef.Name);
        string ns = mdReader.GetString(typeRef.Namespace);
        string fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        return $"{fullType}.{name}";
    }
    if (parent.Kind == HandleKind.TypeDefinition)
    {
        var typeDef = mdReader.GetTypeDefinition((TypeDefinitionHandle)parent);
        string typeName = mdReader.GetString(typeDef.Name);
        string ns = mdReader.GetString(typeDef.Namespace);
        string fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        return $"{fullType}.{name}";
    }
    return name;
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
    // 110x xxxx
    uint val = (uint)(first & 0x1F) << 24;
    val |= (uint)reader.ReadByte(ref offset) << 16;
    val |= (uint)reader.ReadByte(ref offset) << 8;
    val |= reader.ReadByte(ref offset);
    return val;
}

// ─── Module name + metadata tables ──────────────────────────────────────────

static string[] BuildModuleNameTable(StructuralReader reader, PEReader peReader, byte[] image)
{
    int mainAssemblyRefCount = 0;
    MetadataReader mainMetadata = peReader.GetMetadataReader();
    if (!reader.Composite)
    {
        mainAssemblyRefCount = mainMetadata.GetTableRowCount(TableIndex.AssemblyRef);
    }

    ManifestMetadataSection manifest = reader.ManifestMetadata;
    int manifestAssemblyRefCount = 0;
    MetadataReader manifestReader = null;
    if (manifest is not null)
    {
        unsafe
        {
            fixed (byte* ptr = image)
            {
                manifestReader = new MetadataReader(ptr + manifest.FileOffset, manifest.Size);
                manifestAssemblyRefCount = manifestReader.GetTableRowCount(TableIndex.AssemblyRef);
            }
        }
    }

    int totalSlots = mainAssemblyRefCount + 1 + manifestAssemblyRefCount + 1;
    string[] names = new string[totalSlots];

    names[0] = mainMetadata.GetString(mainMetadata.GetAssemblyDefinition().Name) + " (self)";

    for (int i = 1; i <= mainAssemblyRefCount; i++)
    {
        var handle = MetadataTokens.AssemblyReferenceHandle(i);
        names[i] = mainMetadata.GetString(mainMetadata.GetAssemblyReference(handle).Name);
    }

    if (manifestReader is not null)
    {
        for (int i = 0; i < manifestAssemblyRefCount; i++)
        {
            int slot = mainAssemblyRefCount + 1 + i;
            var handle = MetadataTokens.AssemblyReferenceHandle(i + 1);
            string name = manifestReader.GetString(manifestReader.GetAssemblyReference(handle).Name);
            if (slot < names.Length)
                names[slot] = name;
        }
    }

    for (int i = 0; i < names.Length; i++)
        names[i] ??= $"(unresolved #{i})";

    return names;
}

/// <summary>
/// Build a module index → MetadataReader mapping by loading component DLLs from the same directory.
/// The PEReader/byte[] backing data must outlive the returned MetadataReaders.
/// We store them in a list to keep them alive.
/// </summary>
static (Dictionary<int, MetadataReader>, List<(byte[] bytes, PEReader pe)>) BuildModuleMetadata(
    string[] moduleNames, string mainFilePath, PEReader mainPeReader,
    StructuralReader reader, byte[] image)
{
    // Keep PEReaders alive so MetadataReaders stay valid
    var keepAlive = new List<(byte[] bytes, PEReader pe)>();
    var result = new Dictionary<int, MetadataReader>();

    // Module 0: main assembly metadata
    if (mainPeReader.HasMetadata)
        result[0] = mainPeReader.GetMetadataReader();

    string dir = Path.GetDirectoryName(Path.GetFullPath(mainFilePath));

    for (int i = 1; i < moduleNames.Length; i++)
    {
        string name = moduleNames[i];
        if (name.StartsWith('(')) continue; // unresolved/gap

        // Strip any " (self)" suffix
        int parenIdx = name.IndexOf(" (");
        if (parenIdx >= 0) name = name[..parenIdx];

        string dllPath = Path.Combine(dir, name + ".dll");
        if (!File.Exists(dllPath))
            continue;

        try
        {
            byte[] bytes = File.ReadAllBytes(dllPath);
            var pe = new PEReader(new MemoryStream(bytes));
            if (pe.HasMetadata)
            {
                keepAlive.Add((bytes, pe));
                result[i] = pe.GetMetadataReader();
            }
        }
        catch
        {
            // Best effort — skip DLLs we can't open
        }
    }

    GC.KeepAlive(keepAlive);

    return (result, keepAlive);
}
