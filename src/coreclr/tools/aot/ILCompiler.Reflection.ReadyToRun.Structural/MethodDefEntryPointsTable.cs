// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the MethodDefEntryPoints NativeArray section.
    /// Each entry maps a MethodDef RID to its RuntimeFunction index and the
    /// fixup cell references (import section table + cell index pairs) it needs
    /// resolved before execution.
    /// </summary>
    public sealed class MethodDefEntryPointsTable
    {
        public IReadOnlyList<MethodDefEntry> Entries { get; }

        internal MethodDefEntryPointsTable(List<MethodDefEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public MethodDefEntryPointsTable GetMethodDefEntryPointsTable(ReadyToRunSectionHandle section)
        {
            int sectionOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            NativeArray methodEntryPoints = new NativeArray(_nativeReader, (uint)sectionOffset);
            uint count = methodEntryPoints.GetCount();

            var entries = new List<MethodDefEntry>((int)count);

            for (uint rid = 1; rid <= count; rid++)
            {
                int offset = 0;
                if (!methodEntryPoints.TryGetAt(rid - 1, ref offset))
                    continue;

                (RuntimeFunctionIndex runtimeFunctionIndex, List<FixupCellRef> fixupCells) = DecodeRuntimeFunctionIdAndFixupCells(offset);

                entries.Add(new MethodDefEntry(rid, runtimeFunctionIndex, fixupCells));
            }

            return new MethodDefEntryPointsTable(entries);
        }
    }

    /// <summary>
    /// One entry in the MethodDefEntryPoints table: a compiled method identified by
    /// its MethodDef RID, with its RuntimeFunction index and fixup cell references.
    /// Null entries in <see cref="MethodDefEntryPointsTable.Entries"/> indicate
    /// MethodDef RIDs that have no compiled entrypoint.
    /// </summary>
    public sealed class MethodDefEntry
    {
        /// <summary>MethodDef RID (1-based).</summary>
        public uint Rid { get; }

        /// <summary>Index into the RuntimeFunctions array.</summary>
        public RuntimeFunctionIndex EntryPointIndex { get; }

        /// <summary>Fixup cells this method needs resolved before execution.</summary>
        public IReadOnlyList<FixupCellRef> FixupCells { get; }

        public MethodDefEntry(uint rid, RuntimeFunctionIndex entryPointIndex, List<FixupCellRef> fixupCells)
        {
            Rid = rid;
            EntryPointIndex = entryPointIndex;
            FixupCells = fixupCells;
        }
    }

    /// <summary>
    /// A reference to a single fixup cell: identifies the import section and
    /// entry index within that section.
    /// </summary>
    public sealed class FixupCellRef
    {
        /// <summary>Index of the import section in the ImportSections array.</summary>
        public uint TableIndex { get; }

        /// <summary>Index of the entry within the import section.</summary>
        public uint CellIndex { get; }

        public FixupCellRef(uint tableIndex, uint cellIndex)
        {
            TableIndex = tableIndex;
            CellIndex = cellIndex;
        }
    }

    /// <summary>Opaque handle representing an index into the RuntimeFunctions table.</summary>
    public enum RuntimeFunctionIndex {}
}
