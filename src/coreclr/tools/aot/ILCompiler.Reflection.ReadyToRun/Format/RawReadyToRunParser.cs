// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun.Format;

/// <summary>
/// Cross-references R2R section data from a <see cref="ReadyToRunReader"/> (Format) to produce
/// fully materialized, correlated objects. After parsing, no further image byte access is needed.
///
/// <para>
/// This parser requires an <see cref="IAssemblyResolver"/> for module-override signature parsing
/// in composite images, and an existing (old) <see cref="ILCompiler.Reflection.ReadyToRun.ReadyToRunReader"/>
/// for signature decoding infrastructure. A future refactoring may remove the latter dependency.
/// </para>
/// </summary>
public sealed class RawReadyToRunParser
{
    private readonly ReadyToRunReader _formatReader;
    private readonly ILCompiler.Reflection.ReadyToRun.ReadyToRunReader _legacyReader;

    // Cached results
    private IReadOnlyList<ParsedMethod> _methods;
    private IReadOnlyList<ParsedImportSection> _importSections;
    private IReadOnlyList<ParsedAvailableType> _availableTypes;

    // Internal data built during method resolution
    private bool[] _isEntryPoint;
    private Dictionary<int, int> _hotColdMap; // coldRtFuncIdx → hotRtFuncIdx
    private Dictionary<int, int> _rvaToEhInfoRva; // methodStartRva → ehInfoRva

    /// <summary>
    /// Creates a new parser that cross-references sections from the given format reader.
    /// </summary>
    /// <param name="formatReader">The structural format reader (Phase 1).</param>
    /// <param name="legacyReader">The existing ReadyToRunReader for signature decoding infrastructure.</param>
    public RawReadyToRunParser(ReadyToRunReader formatReader, ILCompiler.Reflection.ReadyToRun.ReadyToRunReader legacyReader)
    {
        _formatReader = formatReader;
        _legacyReader = legacyReader;
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
        // (requires parsing the entry points, which we'll do by reading the raw offset data)
        var instanceMethods = _formatReader.InstanceMethodEntryPoints;
        if (instanceMethods != null)
        {
            foreach (var entry in instanceMethods.Entries)
            {
                int rtFuncIdx = GetRuntimeFunctionIndexFromOffset(entry.SignatureBlobOffset);
                if (rtFuncIdx >= 0 && rtFuncIdx < _isEntryPoint.Length)
                    _isEntryPoint[rtFuncIdx] = true;
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
    /// Builds a map from method start RVA to EH info RVA.
    /// </summary>
    private void EnsureEhInfoMap()
    {
        if (_rvaToEhInfoRva != null)
            return;

        _rvaToEhInfoRva = new Dictionary<int, int>();
        var ehInfo = _formatReader.ExceptionInfo;
        if (ehInfo != null)
        {
            foreach (var entry in ehInfo.Entries)
            {
                _rvaToEhInfoRva[entry.MethodRva] = entry.EhInfoRva;
            }
        }
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
    /// Parse the InstanceMethodEntryPoints and create ParsedMethod objects.
    /// </summary>
    private void ParseInstanceMethodEntryPoints(List<ParsedMethod> methods)
    {
        var instanceMethods = _formatReader.InstanceMethodEntryPoints;
        if (instanceMethods == null)
            return;

        foreach (var entry in instanceMethods.Entries)
        {
            int rtFuncIdx = GetRuntimeFunctionIndexFromOffset(entry.SignatureBlobOffset);
            if (rtFuncIdx < 0)
                continue;

            // TODO: Parse the signature blob into an R2RMethodRef using the structural provider
            // For now, create a placeholder
            var runtimeFunctions = CollectRuntimeFunctions(rtFuncIdx);
            var coldFunctions = CollectColdRuntimeFunctions(rtFuncIdx);
            var fixups = ParseFixupCellsFromOffset(entry.SignatureBlobOffset);
            var gcInfo = ParseGcInfo(rtFuncIdx);

            methods.Add(new ParsedMethod(
                rid: 0,
                methodRef: null, // TODO: structural parse
                entryPointRuntimeFunctionIndex: rtFuncIdx,
                runtimeFunctions: runtimeFunctions,
                coldRuntimeFunctions: coldFunctions,
                fixups: fixups,
                gcInfo: gcInfo,
                isInstanceMethod: true,
                componentIndex: 0));
        }
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
        // TODO: Parse unwind info from the image at entry.UnwindRva
        // This requires the image bytes and architecture-specific parsing
        return null;
    }

    private DebugInfo ParseDebugInfo(int runtimeFunctionIndex)
    {
        // TODO: Look up debug info from the sparse array
        return null;
    }

    private EHInfo ParseEhInfo(int startRva)
    {
        if (_rvaToEhInfoRva.TryGetValue(startRva, out int ehInfoRva))
        {
            // TODO: Parse EH clauses from the image at ehInfoRva
            return null;
        }
        return null;
    }

    private BaseGcInfo ParseGcInfo(int entryPointRtFuncIndex)
    {
        // TODO: GC info is at UnwindRVA + UnwindInfo.Size of the first runtime function
        return null;
    }

    // ── Fixup parsing ────────────────────────────────────────────────

    private List<ParsedFixupCell> ParseFixupCells(IReadOnlyList<FixupCellRef> cellRefs)
    {
        var result = new List<ParsedFixupCell>();
        if (cellRefs == null)
            return result;

        foreach (var cell in cellRefs)
        {
            // TODO: Decode signature through import section
            result.Add(new ParsedFixupCell(cell.TableIndex, cell.CellIndex, signature: null));
        }

        return result;
    }

    private List<ParsedFixupCell> ParseFixupCellsFromOffset(int entryPointOffset)
    {
        // TODO: Parse fixup cells from the instance method entry point offset
        return new List<ParsedFixupCell>();
    }

    // ── Import section parsing ───────────────────────────────────────

    private List<ParsedImportSection> ParseImportSections()
    {
        var result = new List<ParsedImportSection>();
        var importSections = _formatReader.ImportSections;
        if (importSections == null)
            return result;

        foreach (var entry in importSections.Entries)
        {
            var entries = new List<ParsedImportEntry>();
            // TODO: Parse per-entry signatures
            result.Add(new ParsedImportSection(entry.Index, entry, entries));
        }

        return result;
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

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a runtime function index from an entry point offset using the same
    /// encoding as MethodDefEntryPoints (compressed uint with fixup bit flags).
    /// </summary>
    private int GetRuntimeFunctionIndexFromOffset(int offset)
    {
        uint id = 0;
        _formatReader.ImageReader.DecodeUnsigned((uint)offset, ref id);

        if ((id & 1) != 0)
        {
            // Has fixups — runtime function index is in upper bits
            return (int)(id >> 2);
        }
        else
        {
            return (int)(id >> 1);
        }
    }
}
