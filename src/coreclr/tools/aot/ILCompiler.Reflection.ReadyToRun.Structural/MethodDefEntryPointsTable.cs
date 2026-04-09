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

        private MethodDefEntryPointsTable(List<MethodDefEntry> entries)
        {
            Entries = entries;
        }

        /// <summary>
        /// Create a table from a pre-built list of entries (used for composite aggregation).
        /// </summary>
        internal static MethodDefEntryPointsTable FromEntries(List<MethodDefEntry> entries)
        {
            return new MethodDefEntryPointsTable(entries);
        }

        /// <summary>
        /// Parse a MethodDefEntryPoints section from the R2R image using a <see cref="ReadyToRunReader"/>.
        /// </summary>
        public static MethodDefEntryPointsTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            return ParseCore(reader.ImageReader, reader.GetOffset(section.RelativeVirtualAddress));
        }

        private static MethodDefEntryPointsTable ParseCore(NativeReader imageReader, int sectionOffset)
        {
            NativeArray methodEntryPoints = new NativeArray(imageReader, (uint)sectionOffset);
            uint count = methodEntryPoints.GetCount();

            var entries = new List<MethodDefEntry>((int)count);

            for (uint rid = 1; rid <= count; rid++)
            {
                int offset = 0;
                if (!methodEntryPoints.TryGetAt(rid - 1, ref offset))
                {
                    continue;
                }

                // Decode the entry point: compressed uint with RuntimeFunction index + fixup flag
                uint id = 0;
                offset = (int)imageReader.DecodeUnsigned((uint)offset, ref id);

                int? fixupOffset = null;
                RuntimeFunctionIndex runtimeFunctionIndex;

                if ((id & 1) != 0)
                {
                    // Has fixups
                    if ((id & 2) != 0)
                    {
                        // Fixup list uses a backward offset
                        uint val = 0;
                        imageReader.DecodeUnsigned((uint)offset, ref val);
                        offset -= (int)val;
                    }

                    fixupOffset = offset;
                    runtimeFunctionIndex = (RuntimeFunctionIndex)(id >> 2);
                }
                else
                {
                    runtimeFunctionIndex = (RuntimeFunctionIndex)(id >> 1);
                }

                // Parse the fixup delay list (nibble-encoded import section/slot pairs)
                var fixupCells = new List<FixupCellRef>();
                if (fixupOffset.HasValue)
                {
                    NibbleReader nibbleReader = new NibbleReader(imageReader, fixupOffset.Value);
                    uint curTableIndex = nibbleReader.ReadUInt();

                    while (true)
                    {
                        uint cellIndex = nibbleReader.ReadUInt();

                        while (true)
                        {
                            fixupCells.Add(new FixupCellRef(curTableIndex, cellIndex));

                            uint delta = nibbleReader.ReadUInt();
                            if (delta == 0)
                                break;

                            cellIndex += delta;
                        }

                        uint tableDelta = nibbleReader.ReadUInt();
                        if (tableDelta == 0)
                            break;

                        curTableIndex += tableDelta;
                    }
                }

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
