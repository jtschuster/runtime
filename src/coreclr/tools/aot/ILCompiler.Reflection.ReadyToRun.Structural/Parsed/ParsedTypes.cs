// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.Reflection.ReadyToRun;


namespace ILCompiler.Reflection.ReadyToRun.Parsed;

/// <summary>
/// A fully cross-referenced method from an R2R image. Contains all structural data
/// (runtime functions, fixups, debug info, EH info, GC info) materialized from the image.
/// Name resolution is deferred to <see cref="ReadyToRunMetadataResolver"/>.
/// </summary>
public sealed class ParsedMethod
{
    /// <summary>
    /// MethodDef RID for methods from the MethodDefEntryPoints table.
    /// 0 for instance methods that come from InstanceMethodEntryPoints.
    /// </summary>
    public uint Rid { get; }

    /// <summary>
    /// Structural method reference for instance methods (parsed from signature blob).
    /// Null for MethodDef methods (use Rid instead).
    /// </summary>
    public R2RMethodRef MethodRef { get; }

    /// <summary>
    /// Index of the entry-point runtime function in the RuntimeFunctions table.
    /// </summary>
    public int EntryPointRuntimeFunctionIndex { get; }

    /// <summary>
    /// All runtime functions for this method (hot region).
    /// </summary>
    public IReadOnlyList<ParsedRuntimeFunction> RuntimeFunctions { get; }

    /// <summary>
    /// Cold runtime functions for this method (from HotColdMap), or empty.
    /// </summary>
    public IReadOnlyList<ParsedRuntimeFunction> ColdRuntimeFunctions { get; }

    /// <summary>
    /// Fixup cells referenced by this method.
    /// </summary>
    public IReadOnlyList<ParsedFixupCell> Fixups { get; }

    /// <summary>
    /// GC info for this method, or null if not available.
    /// Derived from the first runtime function's unwind info.
    /// </summary>
    public BaseGcInfo GcInfo { get; }

    /// <summary>
    /// Whether this is an instance method (from InstanceMethodEntryPoints).
    /// </summary>
    public bool IsInstanceMethod { get; }

    /// <summary>
    /// Assembly index for composite images, or 0 for single-file images.
    /// </summary>
    public int ComponentIndex { get; }

    public ParsedMethod(
        uint rid,
        R2RMethodRef methodRef,
        int entryPointRuntimeFunctionIndex,
        IReadOnlyList<ParsedRuntimeFunction> runtimeFunctions,
        IReadOnlyList<ParsedRuntimeFunction> coldRuntimeFunctions,
        IReadOnlyList<ParsedFixupCell> fixups,
        BaseGcInfo gcInfo,
        bool isInstanceMethod,
        int componentIndex)
    {
        Rid = rid;
        MethodRef = methodRef;
        EntryPointRuntimeFunctionIndex = entryPointRuntimeFunctionIndex;
        RuntimeFunctions = runtimeFunctions;
        ColdRuntimeFunctions = coldRuntimeFunctions;
        Fixups = fixups;
        GcInfo = gcInfo;
        IsInstanceMethod = isInstanceMethod;
        ComponentIndex = componentIndex;
    }
}

/// <summary>
/// A runtime function with its associated unwind, debug, and exception handling info.
/// </summary>
public sealed class ParsedRuntimeFunction
{
    /// <summary>Index in the RuntimeFunctions table.</summary>
    public int Index { get; }

    /// <summary>Start RVA of the code.</summary>
    public int StartRva { get; }

    /// <summary>End RVA of the code (Amd64 only; -1 otherwise).</summary>
    public int EndRva { get; }

    /// <summary>Size in bytes of the code region.</summary>
    public int Size { get; }

    /// <summary>RVA of the unwind data.</summary>
    public int UnwindRva { get; }

    /// <summary>Offset of this function's code relative to the method's entry point.</summary>
    public int CodeOffset { get; }

    /// <summary>Parsed unwind information, or null if not available.</summary>
    public BaseUnwindInfo UnwindInfo { get; }

    /// <summary>Parsed debug information (bounds and variables), or null.</summary>
    public DebugInfo DebugInfo { get; }

    /// <summary>Parsed exception handling clauses, or null.</summary>
    public EHInfo EHInfo { get; }

    public ParsedRuntimeFunction(
        int index,
        int startRva,
        int endRva,
        int size,
        int unwindRva,
        int codeOffset,
        BaseUnwindInfo unwindInfo,
        DebugInfo debugInfo,
        EHInfo ehInfo)
    {
        Index = index;
        StartRva = startRva;
        EndRva = endRva;
        Size = size;
        UnwindRva = unwindRva;
        CodeOffset = codeOffset;
        UnwindInfo = unwindInfo;
        DebugInfo = debugInfo;
        EHInfo = ehInfo;
    }
}

/// <summary>
/// A fixup cell with its decoded signature AST.
/// </summary>
public sealed class ParsedFixupCell
{
    /// <summary>Import section table index.</summary>
    public uint TableIndex { get; }

    /// <summary>Cell index within the import section.</summary>
    public uint CellIndex { get; }

    /// <summary>Decoded fixup signature, or null if decoding failed.</summary>
    public R2RFixupSignature Signature { get; }

    public ParsedFixupCell(uint tableIndex, uint cellIndex, R2RFixupSignature signature)
    {
        TableIndex = tableIndex;
        CellIndex = cellIndex;
        Signature = signature;
    }
}

/// <summary>
/// An import section with its decoded per-entry signatures.
/// </summary>
public sealed class ParsedImportSection
{
    /// <summary>Index of this import section.</summary>
    public int Index { get; }

    /// <summary>The raw import section entry from the Format reader.</summary>
    public ImportSectionEntry RawEntry { get; }

    /// <summary>Decoded entries with their fixup signatures.</summary>
    public IReadOnlyList<ParsedImportEntry> Entries { get; }

    public ParsedImportSection(int index, ImportSectionEntry rawEntry, IReadOnlyList<ParsedImportEntry> entries)
    {
        Index = index;
        RawEntry = rawEntry;
        Entries = entries;
    }
}

/// <summary>
/// A single entry within an import section with its decoded signature.
/// </summary>
public sealed class ParsedImportEntry
{
    /// <summary>Index of this entry within the import section.</summary>
    public int Index { get; }

    /// <summary>RVA of this entry's slot.</summary>
    public int Rva { get; }

    /// <summary>Decoded fixup signature for this entry, or null if decoding failed.</summary>
    public R2RFixupSignature Signature { get; }

    public ParsedImportEntry(int index, int rva, R2RFixupSignature signature)
    {
        Index = index;
        Rva = rva;
        Signature = signature;
    }
}

/// <summary>
/// An available type entry with optional name resolution.
/// </summary>
public sealed class ParsedAvailableType
{
    /// <summary>TypeDef or ExportedType RID.</summary>
    public uint Rid { get; }

    /// <summary>Whether this is an exported type (true) or a TypeDef (false).</summary>
    public bool IsExported { get; }

    /// <summary>Assembly index for composite images.</summary>
    public int ComponentIndex { get; }

    public ParsedAvailableType(uint rid, bool isExported, int componentIndex)
    {
        Rid = rid;
        IsExported = isExported;
        ComponentIndex = componentIndex;
    }
}
