// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;

using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// A raw reference to a slot in an import section, decoded from the
    /// MethodDefEntryPoints fixup blob. Unlike <see cref="FixupCell"/>, this type
    /// contains only the structural indices without a resolved signature.
    /// </summary>
    public readonly struct FixupCellRef
    {
        /// <summary>
        /// Zero-based index of the import section containing this cell.
        /// </summary>
        public int ImportSectionIndex { get; }

        /// <summary>
        /// Zero-based slot index within the import section.
        /// </summary>
        public int SlotIndex { get; }

        internal FixupCellRef(int importSectionIndex, int slotIndex)
        {
            ImportSectionIndex = importSectionIndex;
            SlotIndex = slotIndex;
        }
    }

    /// <summary>
    /// A direct projection of a single entry from the MethodDefEntryPoints section.
    /// </summary>
    public readonly struct MethodDefEntryPoint
    {
        /// <summary>
        /// Zero-based index into the RuntimeFunctions table for this method's entry point.
        /// </summary>
        public int RuntimeFunctionIndex { get; }

        /// <summary>
        /// The list of fixup cells that must be initialized before this method can execute,
        /// or <see langword="null"/> if the method has no fixups.
        /// </summary>
        public IReadOnlyList<FixupCellRef>? Fixups { get; }

        internal MethodDefEntryPoint(int runtimeFunctionIndex, IReadOnlyList<FixupCellRef>? fixups)
        {
            RuntimeFunctionIndex = runtimeFunctionIndex;
            Fixups = fixups;
        }
    }

    /// <summary>
    /// A direct projection of the <see cref="ReadyToRunSectionType.MethodDefEntryPoints"/> section.
    /// Maps MethodDef row IDs (1-based) to runtime function indices and optional fixup cell lists.
    /// </summary>
    public sealed class MethodDefEntryPointsTable
    {
        private readonly MethodDefEntryPoint?[] _entries;

        private MethodDefEntryPointsTable(MethodDefEntryPoint?[] entries)
        {
            _entries = entries;
        }

        /// <summary>
        /// The number of MethodDef row IDs covered by this table. Valid RIDs are in the range [1, Count].
        /// </summary>
        public int Count => _entries.Length;

        /// <summary>
        /// Tries to get the entry point for the given MethodDef RID (1-based).
        /// Returns <see langword="false"/> when no compiled entry point exists for the given method.
        /// </summary>
        public bool TryGetEntryPoint(int rid, out MethodDefEntryPoint entryPoint)
        {
            if (rid < 1 || rid > _entries.Length)
            {
                entryPoint = default;
                return false;
            }

            MethodDefEntryPoint? entry = _entries[rid - 1];
            if (entry is null)
            {
                entryPoint = default;
                return false;
            }

            entryPoint = entry.Value;
            return true;
        }

        /// <summary>
        /// Enumerates all methods that have compiled entry points, returning each (RID, entry point) pair.
        /// RIDs are 1-based.
        /// </summary>
        public IEnumerable<(int Rid, MethodDefEntryPoint EntryPoint)> GetDefinedEntryPoints()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i] is { } entry)
                {
                    yield return (i + 1, entry);
                }
            }
        }

        internal static MethodDefEntryPointsTable Parse(NativeReader imageReader, int sectionOffset)
        {
            NativeArray methodEntryPoints = new NativeArray(imageReader, (uint)sectionOffset);
            uint count = methodEntryPoints.GetCount();

            MethodDefEntryPoint?[] entries = new MethodDefEntryPoint?[count];

            for (uint rid = 1; rid <= count; rid++)
            {
                int entryOffset = 0;
                if (!methodEntryPoints.TryGetAt(rid - 1, ref entryOffset))
                {
                    continue;
                }

                // Decode the runtime function index and optional fixup offset.
                // The value is: (runtimeFunctionIndex << 1) | hasFixups
                // or:           (runtimeFunctionIndex << 2) | 0x3  with a preceding fixup delta
                uint id = 0;
                int nextOffset = (int)imageReader.DecodeUnsigned((uint)entryOffset, ref id);

                int? fixupOffset = null;
                if ((id & 1) != 0)
                {
                    if ((id & 2) != 0)
                    {
                        uint val = 0;
                        imageReader.DecodeUnsigned((uint)nextOffset, ref val);
                        nextOffset -= (int)val;
                    }
                    fixupOffset = nextOffset;
                    id >>= 2;
                }
                else
                {
                    id >>= 1;
                }

                IReadOnlyList<FixupCellRef>? fixups = null;
                if (fixupOffset.HasValue)
                {
                    fixups = ParseFixupCells(imageReader, fixupOffset.Value);
                }

                entries[rid - 1] = new MethodDefEntryPoint((int)id, fixups);
            }

            return new MethodDefEntryPointsTable(entries);
        }

        private static List<FixupCellRef> ParseFixupCells(NativeReader imageReader, int fixupOffset)
        {
            // Algorithm ported from CoreCLR src\vm\ceeload.inl, Module::FixupDelayListAux
            var cells = new List<FixupCellRef>();
            NibbleReader reader = new NibbleReader(imageReader, fixupOffset);

            uint curTableIndex = reader.ReadUInt();

            while (true)
            {
                uint fixupIndex = reader.ReadUInt();

                while (true)
                {
                    cells.Add(new FixupCellRef((int)curTableIndex, (int)fixupIndex));

                    uint delta = reader.ReadUInt();
                    if (delta == 0)
                        break;

                    fixupIndex += delta;
                }

                uint tableIndex = reader.ReadUInt();
                if (tableIndex == 0)
                    break;

                curTableIndex += tableIndex;
            }

            return cells;
        }
    }
}
