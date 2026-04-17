// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun.Parsed;

/// <summary>
/// Cross-references R2R section data from a <see cref="ReadyToRunReader"/> (Format) to produce
/// fully materialized, correlated objects. After parsing, no further image byte access is needed.
///
/// <para>
/// This parser is fully standalone — it does not depend on any legacy libraries.
/// </para>
/// </summary>
public sealed class RawReadyToRunParser
{
    private readonly ReadyToRunReader _formatReader;

    // Cached results
    private IReadOnlyList<ParsedMethod> _methods;
    private IReadOnlyList<ParsedImportSection> _importSections;
    private IReadOnlyList<ParsedAvailableType> _availableTypes;
    private IReadOnlyList<ParsedPgoInfo> _pgoInfos;

    // Internal data built during method resolution
    private bool[] _isEntryPoint;
    private Dictionary<int, int> _hotColdMap; // coldRtFuncIdx → hotRtFuncIdx
    private Dictionary<int, int> _rvaToEhInfoRva; // methodStartRva → ehInfoRva
    private Dictionary<int, int> _ehClauseCounts; // methodStartRva → clauseCount

    /// <summary>
    /// Creates a new parser that cross-references sections from the given format reader.
    /// </summary>
    /// <param name="formatReader">The structural format reader.</param>
    public RawReadyToRunParser(ReadyToRunReader formatReader)
    {
        _formatReader = formatReader;
    }

    /// <summary>The underlying format reader.</summary>
    public ReadyToRunReader FormatReader => _formatReader;

    /// <summary>
    /// Gets all methods cross-referenced from MethodDefEntryPoints and InstanceMethodEntryPoints.
    /// Each method includes its runtime functions, fixups, debug info, EH info, and GC info.
    /// </summary>
    public IReadOnlyList<ParsedMethod> GetMethods()
    {
        if (_methods != null)
            return _methods;

        EnsureEntryPointMap();
        EnsureHotColdMap();
        EnsureEhInfoMap();
        EnsureDebugInfoOffsets();

        var methods = new List<ParsedMethod>();

        ParseMethodDefEntryPoints(methods);
        ParseInstanceMethodEntryPoints(methods);

        _methods = methods;
        return _methods;
    }

    /// <summary>
    /// Gets all import sections with their decoded per-entry fixup signatures.
    /// </summary>
    public IReadOnlyList<ParsedImportSection> GetImportSections()
    {
        if (_importSections != null)
            return _importSections;

        _importSections = ParseImportSections();
        return _importSections;
    }

    /// <summary>
    /// Gets all available types from the AvailableTypes hashtable.
    /// </summary>
    public IReadOnlyList<ParsedAvailableType> GetAvailableTypes()
    {
        if (_availableTypes != null)
            return _availableTypes;

        _availableTypes = ParseAvailableTypes();
        return _availableTypes;
    }

    /// <summary>
    /// Gets all PGO instrumentation data entries.
    /// Each entry contains a method reference and its PGO schema elements.
    /// </summary>
    public IReadOnlyList<ParsedPgoInfo> GetPgoInfos()
    {
        if (_pgoInfos != null)
            return _pgoInfos;

        _pgoInfos = ParsedPgoInfo.ParseAll(_formatReader, CreateStructuralDecoder, LookupImportSignature);
        return _pgoInfos;
    }

    // ── Internal parsing methods ─────────────────────────────────────

    /// <summary>
    /// Builds the isEntryPoint[] boolean array that marks which runtime functions
    /// are the first function of a method.
    /// </summary>
    private void EnsureEntryPointMap()
    {
        if (_isEntryPoint != null)
            return;

        var rtFuncs = _formatReader.RuntimeFunctions;
        if (rtFuncs == null)
        {
            _isEntryPoint = Array.Empty<bool>();
            return;
        }

        _isEntryPoint = new bool[rtFuncs.Entries.Count];

        // Mark entry points from MethodDefEntryPoints
        var methodDefs = _formatReader.MethodDefEntryPoints;
        if (methodDefs != null)
        {
            foreach (var entry in methodDefs.Entries)
            {
                if (entry.EntryPointIndex >= 0 && entry.EntryPointIndex < _isEntryPoint.Length)
                    _isEntryPoint[entry.EntryPointIndex] = true;
            }
        }

        // Mark entry points from InstanceMethodEntryPoints
        // Parse each entry's signature blob to find the entry point descriptor that follows.
        var instanceEntries = _formatReader.InstanceMethodEntryPoints?.Entries;
        if (instanceEntries is not null)
        {
            foreach (var entry in instanceEntries)
            {
                try
                {
                    var decoder = CreateStructuralDecoder(entry.SignatureBlobOffset);
                    decoder.ParseMethod();
                    _formatReader.GetRuntimeFunctionIndexFromOffset(decoder.Offset, out int rtFuncIdx, out _);
                    if (rtFuncIdx >= 0 && rtFuncIdx < _isEntryPoint.Length)
                        _isEntryPoint[rtFuncIdx] = true;
                }
                catch
                {
                    // Skip entries that fail to parse
                }
            }
        }
    }

    /// <summary>
    /// Builds a map from cold runtime function index to hot runtime function index.
    /// </summary>
    private void EnsureHotColdMap()
    {
        if (_hotColdMap != null)
            return;

        _hotColdMap = new Dictionary<int, int>();
        var hotCold = _formatReader.HotColdMap;
        if (hotCold != null)
        {
            foreach (var entry in hotCold.Entries)
            {
                _hotColdMap[entry.ColdRuntimeFunctionIndex] = entry.HotRuntimeFunctionIndex;
                // Also mark cold functions as NOT entry points
                if (_isEntryPoint != null && entry.ColdRuntimeFunctionIndex < _isEntryPoint.Length)
                    _isEntryPoint[entry.ColdRuntimeFunctionIndex] = false;
            }
        }
    }

    /// <summary>
    /// Builds maps from method start RVA to EH info RVA and clause counts.
    /// Clause count is derived from the difference between consecutive EH info RVAs.
    /// </summary>
    private void EnsureEhInfoMap()
    {
        if (_rvaToEhInfoRva != null)
            return;

        _rvaToEhInfoRva = new Dictionary<int, int>();
        _ehClauseCounts = new Dictionary<int, int>();
        var ehInfo = _formatReader.ExceptionInfo;
        if (ehInfo == null || ehInfo.Entries.Count == 0)
            return;

        var entries = ehInfo.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            _rvaToEhInfoRva[entries[i].MethodRva] = entries[i].EhInfoRva;
        }

        // Clause count = (nextEhInfoRva - thisEhInfoRva) / EHClause.Length
        for (int i = 0; i < entries.Count - 1; i++)
        {
            int clauseCount = (entries[i + 1].EhInfoRva - entries[i].EhInfoRva) / EHClause.Length;
            _ehClauseCounts[entries[i].MethodRva] = clauseCount;
        }
        // Last entry: we can't determine clause count from the next entry,
        // so we leave it without a count (it won't be parsed).
        // A more complete solution would use the section end offset.
    }

    /// <summary>
    /// Parse the MethodDef entry points and create ParsedMethod objects.
    /// </summary>
    private void ParseMethodDefEntryPoints(List<ParsedMethod> methods)
    {
        var methodDefs = _formatReader.MethodDefEntryPoints;
        if (methodDefs == null)
            return;

        foreach (var entry in methodDefs.Entries)
        {
            int rtFuncIdx = (int)entry.EntryPointIndex;
            if (rtFuncIdx < 0)
                continue;

            var runtimeFunctions = CollectRuntimeFunctions(rtFuncIdx);
            var coldFunctions = CollectColdRuntimeFunctions(rtFuncIdx);
            var fixups = ParseFixupCells(entry.FixupCells);
            var gcInfo = ParseGcInfo(rtFuncIdx);

            methods.Add(new ParsedMethod(
                rid: entry.Rid,
                methodRef: null,
                entryPointRuntimeFunctionIndex: rtFuncIdx,
                runtimeFunctions: runtimeFunctions,
                coldRuntimeFunctions: coldFunctions,
                fixups: fixups,
                gcInfo: gcInfo,
                isInstanceMethod: false,
                componentIndex: 0));
        }
    }

    /// <summary>
    /// Parse the InstanceMethodEntryPoints by decoding each entry's signature blob and
    /// entry point descriptor. Fully standalone — no legacy reader dependency.
    /// </summary>
    private void ParseInstanceMethodEntryPoints(List<ParsedMethod> methods)
    {
        var entries = _formatReader.InstanceMethodEntryPoints?.Entries;
        if (entries is null)
            return;

        foreach (var entry in entries)
        {
            try
            {
                int offset = entry.SignatureBlobOffset;
                var decoder = CreateStructuralDecoder(offset);
                var methodRef = decoder.ParseMethod();

                // After the method signature, the stream contains an encoded entry point descriptor.
                // GetRuntimeFunctionIndexFromOffset decodes it (bit 0 = fixups present,
                // bit 1 = delta-encoded fixup offset, remaining bits = runtime function index).
                _formatReader.GetRuntimeFunctionIndexFromOffset(decoder.Offset, out int rtFuncIdx, out int? fixupOffset);

                if (rtFuncIdx < 0)
                    continue;

                var runtimeFunctions = CollectRuntimeFunctions(rtFuncIdx);
                var coldFunctions = CollectColdRuntimeFunctions(rtFuncIdx);
                var gcInfo = ParseGcInfo(rtFuncIdx);

                var fixups = fixupOffset.HasValue
                    ? CollectFixupsFromOffset(fixupOffset.Value)
                    : new List<ParsedFixupCell>();

                methods.Add(new ParsedMethod(
                    rid: 0, // Instance methods don't have a MethodDef RID in the entry point table
                    methodRef: methodRef,
                    entryPointRuntimeFunctionIndex: rtFuncIdx,
                    runtimeFunctions: runtimeFunctions,
                    coldRuntimeFunctions: coldFunctions,
                    fixups: fixups,
                    gcInfo: gcInfo,
                    isInstanceMethod: true,
                    componentIndex: 0));
            }
            catch
            {
                // Skip entries that fail to parse
            }
        }
    }

    /// <summary>
    /// Collect fixup cells starting at a given fixup blob offset in the image.
    /// The fixup blob uses nibble-encoded delta lists — see Module::FixupDelayListAux in the VM.
    /// </summary>
    private List<ParsedFixupCell> CollectFixupsFromOffset(int fixupOffset)
    {
        var result = new List<ParsedFixupCell>();
        try
        {
            var importSections = GetImportSections();
            var nibbleReader = new NibbleReader(_formatReader.ImageReader, fixupOffset);

            uint curTableIndex = nibbleReader.ReadUInt();

            while (true)
            {
                uint fixupIndex = nibbleReader.ReadUInt();

                while (true)
                {
                    R2RFixupSignature signature = null;
                    if ((int)curTableIndex < importSections.Count)
                    {
                        var section = importSections[(int)curTableIndex];
                        if ((int)fixupIndex < section.Entries.Count)
                        {
                            signature = section.Entries[(int)fixupIndex].Signature;
                        }
                    }

                    result.Add(new ParsedFixupCell(curTableIndex, fixupIndex, signature));

                    uint delta = nibbleReader.ReadUInt();
                    if (delta == 0)
                        break;

                    fixupIndex += delta;
                }

                uint tableIndex = nibbleReader.ReadUInt();
                if (tableIndex == 0)
                    break;

                curTableIndex = curTableIndex + tableIndex;
            }
        }
        catch
        {
            // Fixup parsing can fail for some methods
        }
        return result;
    }

    /// <summary>
    /// Collect the hot runtime functions belonging to a method starting at the given entry point index.
    /// Walks forward until the next entry point boundary.
    /// </summary>
    private List<ParsedRuntimeFunction> CollectRuntimeFunctions(int entryPointIndex)
    {
        var result = new List<ParsedRuntimeFunction>();
        var rtFuncs = _formatReader.RuntimeFunctions;
        if (rtFuncs == null)
            return result;

        int methodStartRva = rtFuncs.Entries[entryPointIndex].StartRva;

        for (int i = entryPointIndex; i < rtFuncs.Entries.Count; i++)
        {
            // Stop at next method's entry point (unless this is the first function)
            if (i > entryPointIndex && _isEntryPoint[i])
                break;

            // Skip cold functions (they belong to HotColdMap)
            if (_hotColdMap.ContainsKey(i))
                continue;

            var entry = rtFuncs.Entries[i];
            int codeOffset = entry.StartRva - methodStartRva;

            // Parse unwind, debug, and EH info
            var unwindInfo = ParseUnwindInfo(entry);
            var debugInfo = ParseDebugInfo(i);
            var ehInfo = ParseEhInfo(entry.StartRva);

            int size = CalculateRuntimeFunctionSize(entry, i);

            result.Add(new ParsedRuntimeFunction(
                index: i,
                startRva: entry.StartRva,
                endRva: entry.EndRva ?? -1,
                size: size,
                unwindRva: entry.UnwindRva,
                codeOffset: codeOffset,
                unwindInfo: unwindInfo,
                debugInfo: debugInfo,
                ehInfo: ehInfo));
        }

        return result;
    }

    /// <summary>
    /// Collect cold runtime functions for a method via the HotColdMap.
    /// </summary>
    private List<ParsedRuntimeFunction> CollectColdRuntimeFunctions(int hotEntryPointIndex)
    {
        var result = new List<ParsedRuntimeFunction>();
        var rtFuncs = _formatReader.RuntimeFunctions;
        if (rtFuncs == null)
            return result;

        // Find all cold functions that map back to runtime functions owned by this method
        foreach (var kvp in _hotColdMap)
        {
            int coldIdx = kvp.Key;
            int hotIdx = kvp.Value;

            // Check if this cold function's hot counterpart belongs to this method
            if (hotIdx >= hotEntryPointIndex)
            {
                // Check that hotIdx is actually part of this method (before next entry point)
                bool belongsToMethod = true;
                for (int i = hotEntryPointIndex + 1; i <= hotIdx && i < _isEntryPoint.Length; i++)
                {
                    if (_isEntryPoint[i])
                    {
                        belongsToMethod = false;
                        break;
                    }
                }

                if (belongsToMethod)
                {
                    var entry = rtFuncs.Entries[coldIdx];
                    int size = CalculateRuntimeFunctionSize(entry, coldIdx);
                    var unwindInfo = ParseUnwindInfo(entry);

                    result.Add(new ParsedRuntimeFunction(
                        index: coldIdx,
                        startRva: entry.StartRva,
                        endRva: entry.EndRva ?? -1,
                        size: size,
                        unwindRva: entry.UnwindRva,
                        codeOffset: 0,
                        unwindInfo: unwindInfo,
                        debugInfo: null,
                        ehInfo: null));
                }
            }
        }

        return result;
    }

    private int CalculateRuntimeFunctionSize(RuntimeFunctionEntry entry, int index)
    {
        if (entry.EndRva.HasValue && entry.EndRva.Value > 0)
            return entry.EndRva.Value - entry.StartRva;

        // On non-Amd64, estimate size from next runtime function
        var rtFuncs = _formatReader.RuntimeFunctions;
        if (index + 1 < rtFuncs.Entries.Count)
            return rtFuncs.Entries[index + 1].StartRva - entry.StartRva;

        return -1;
    }

    // ── Unwind / Debug / EH / GC parsing ─────────────────────────────

    private BaseUnwindInfo ParseUnwindInfo(RuntimeFunctionEntry entry)
    {
        try
        {
            int unwindOffset = _formatReader.GetOffset(entry.UnwindRva);
            NativeReader imageReader = _formatReader.ImageReader;

            return _formatReader.Machine switch
            {
                Machine.I386 => new x86.UnwindInfo(imageReader, unwindOffset),
                Machine.Amd64 => new Amd64.UnwindInfo(imageReader, unwindOffset),
                Machine.ArmThumb2 => new Arm.UnwindInfo(imageReader, unwindOffset),
                Machine.Arm64 => new Arm64.UnwindInfo(imageReader, unwindOffset),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<uint, int> _debugInfoOffsets; // rtFuncIndex → file offset

    private void EnsureDebugInfoOffsets()
    {
        if (_debugInfoOffsets != null)
            return;

        _debugInfoOffsets = new Dictionary<uint, int>();
        var debugInfo = _formatReader.DebugInfo;
        if (debugInfo != null)
        {
            foreach (var entry in debugInfo.Entries)
            {
                _debugInfoOffsets[entry.RuntimeFunctionIndex] = (int)entry.DebugInfoOffset;
            }
        }
    }

    private DebugInfo ParseDebugInfo(int runtimeFunctionIndex)
    {
        int offset = GetDebugInfoOffset(runtimeFunctionIndex);
        if (offset < 0)
            return null;

        return _formatReader.GetDebugInfo((DebugInfoHandle)offset);
    }

    /// <summary>
    /// Gets the debug info file offset for a given runtime function index, or -1 if none.
    /// This can be used by consumers who need to parse debug info with additional context.
    /// </summary>
    public int GetDebugInfoOffset(int runtimeFunctionIndex)
    {
        EnsureDebugInfoOffsets();
        if (_debugInfoOffsets.TryGetValue((uint)runtimeFunctionIndex, out int offset))
            return offset;
        return -1;
    }

    private EHInfo ParseEhInfo(int startRva)
    {
        if (!_rvaToEhInfoRva.TryGetValue(startRva, out int ehInfoRva))
            return null;

        if (!_ehClauseCounts.TryGetValue(startRva, out int clauseCount) || clauseCount <= 0)
            return null;

        try
        {
            int offset = _formatReader.GetOffset(ehInfoRva);
            return new EHInfo(_formatReader.ImageReader, ehInfoRva, startRva, offset, clauseCount);
        }
        catch
        {
            return null;
        }
    }

    private BaseGcInfo ParseGcInfo(int entryPointRtFuncIndex)
    {
        // GC info sits right after the unwind info of the first runtime function:
        // - On I386: GcInfoRva = UnwindRva (same offset)
        // - On other archs: GcInfoRva = UnwindRva + UnwindInfo.Size
        var rtFuncs = _formatReader.RuntimeFunctions;
        if (rtFuncs == null || entryPointRtFuncIndex >= rtFuncs.Entries.Count)
            return null;

        var entry = rtFuncs.Entries[entryPointRtFuncIndex];
        NativeReader imageReader = _formatReader.ImageReader;

        try
        {
            int gcInfoRva;
            if (_formatReader.Machine == Machine.I386)
            {
                gcInfoRva = entry.UnwindRva;
            }
            else
            {
                var unwindInfo = ParseUnwindInfo(entry);
                if (unwindInfo == null)
                    return null;
                gcInfoRva = entry.UnwindRva + unwindInfo.Size;
            }

            int gcInfoOffset = _formatReader.GetOffset(gcInfoRva);

            if (_formatReader.Machine == Machine.I386)
            {
                return new x86.GcInfo(imageReader, gcInfoOffset);
            }
            else
            {
                return new Amd64.GcInfo(
                    imageReader,
                    gcInfoOffset,
                    _formatReader.Machine,
                    _formatReader.ReadyToRunHeader.MajorVersion,
                    _formatReader.ReadyToRunHeader.MinorVersion);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── Fixup parsing ────────────────────────────────────────────────

    private List<ParsedFixupCell> ParseFixupCells(IReadOnlyList<FixupCellRef> cellRefs)
    {
        var result = new List<ParsedFixupCell>();
        if (cellRefs == null)
            return result;

        // Ensure import sections are parsed so we can look up signature RVAs
        var importSections = GetImportSections();

        foreach (var cell in cellRefs)
        {
            R2RFixupSignature signature = null;
            int tableIdx = (int)cell.TableIndex;
            int cellIdx = (int)cell.CellIndex;

            if (tableIdx < importSections.Count)
            {
                var section = importSections[tableIdx];
                if (cellIdx < section.Entries.Count)
                {
                    signature = section.Entries[cellIdx].Signature;
                }
            }

            result.Add(new ParsedFixupCell(cell.TableIndex, cell.CellIndex, signature));
        }

        return result;
    }

    // ── Import section parsing ───────────────────────────────────────

    private List<ParsedImportSection> ParseImportSections()
    {
        var result = new List<ParsedImportSection>();
        var importSections = _formatReader.ImportSections;
        if (importSections == null)
            return result;

        foreach (var section in importSections.Entries)
        {
            var entries = ParseImportSectionEntries(section);
            result.Add(new ParsedImportSection(section.Index, section, entries));
        }

        return result;
    }

    private List<ParsedImportEntry> ParseImportSectionEntries(ImportSectionEntry section)
    {
        var entries = new List<ParsedImportEntry>();
        if ((int)section.SignatureTableRva == 0 || section.EntryCount == 0)
            return entries;

        try
        {
            int signatureOffset = _formatReader.GetOffset((int)section.SignatureTableRva);
            int sectionOffset = _formatReader.GetOffset((int)section.SectionRva);

            for (int i = 0; i < section.EntryCount; i++)
            {
                int entryRva = (int)section.SectionRva + section.EntrySize * i;
                R2RFixupSignature signature = null;

                // Each entry in the signature table is a 4-byte RVA pointing to a signature blob
                int sigTableOffset = signatureOffset + i * sizeof(int);
                uint sigRva = _formatReader.ImageReader.ReadUInt32(ref sigTableOffset);

                if (sigRva != 0)
                {
                    try
                    {
                        int sigOffset = _formatReader.GetOffset((int)sigRva);
                        signature = ParseFixupSignatureAtOffset(sigOffset);
                    }
                    catch
                    {
                        // Signature parsing can fail for unsupported fixup kinds
                    }
                }

                entries.Add(new ParsedImportEntry(i, entryRva, signature));
            }
        }
        catch
        {
            // Section parsing can fail for malformed images
        }

        return entries;
    }

    /// <summary>
    /// Parse a fixup signature blob at the given image offset into an <see cref="R2RFixupSignature"/> AST.
    /// Uses <see cref="StructuralSignatureDecoder"/> which produces structural AST nodes
    /// (<see cref="R2RTypeNode"/>, <see cref="R2RMethodRef"/>, <see cref="R2RFieldRef"/>)
    /// for the payload.
    /// </summary>
    private R2RFixupSignature ParseFixupSignatureAtOffset(int offset)
    {
        try
        {
            var decoder = CreateStructuralDecoder(offset);
            return decoder.ParseFixupSignature();
        }
        catch
        {
            return null;
        }
    }

    private R2RStructuralContext _structuralContext;

    private StructuralSignatureDecoder CreateStructuralDecoder(int offset)
    {
        if (_structuralContext is null)
        {
            _structuralContext = new R2RStructuralContext(readerToModuleIndex: null);
        }

        var globalMetadata = _formatReader.GetGlobalMetadata();
        return new StructuralSignatureDecoder(
            _structuralContext,
            globalMetadata?.MetadataReader,
            _formatReader,
            offset);
    }

    /// <summary>
    /// Lookup a parsed import signature by table index and fixup index.
    /// Used by PGO info parsing to resolve import references.
    /// </summary>
    private R2RFixupSignature LookupImportSignature(int tableIndex, int fixupIndex)
    {
        var importSections = GetImportSections();
        if (tableIndex < importSections.Count)
        {
            var section = importSections[tableIndex];
            if (fixupIndex < section.Entries.Count)
            {
                return section.Entries[fixupIndex].Signature;
            }
        }
        return null;
    }

    // ── Available types parsing ──────────────────────────────────────

    private List<ParsedAvailableType> ParseAvailableTypes()
    {
        var result = new List<ParsedAvailableType>();
        var availableTypes = _formatReader.AvailableTypes;
        if (availableTypes == null)
            return result;

        foreach (var entry in availableTypes.Entries)
        {
            result.Add(new ParsedAvailableType(entry.Rid, entry.IsExportedType, componentIndex: 0));
        }

        return result;
    }
}
