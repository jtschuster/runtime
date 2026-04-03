// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the InliningInfo section (v1, section 110, deprecated in 4.1).
    /// Contains an index of inlinee RIDs to nibble-encoded lists of inliner RIDs.
    /// No method name resolution is performed.
    /// </summary>
    public sealed class RawInliningInfoTable
    {
        public IReadOnlyList<RawInliningInfoEntry> Entries { get; }

        private RawInliningInfoTable(List<RawInliningInfoEntry> entries)
        {
            Entries = entries;
        }

        public static RawInliningInfoTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int startOffset = reader.GetOffset(section.RelativeVirtualAddress);
            int offset = startOffset;
            int sizeOfInlineIndex = reader.ImageReader.ReadInt32(ref offset);
            int inlineIndexEndOffset = offset + sizeOfInlineIndex;
            var entries = new List<RawInliningInfoEntry>();

            while (offset < inlineIndexEndOffset)
            {
                int inlineeRid = reader.ImageReader.ReadInt32(ref offset);
                int inlinersRelativeOffset = reader.ImageReader.ReadInt32(ref offset);

                var nibbleReader = new NibbleReader(reader.ImageReader, inlineIndexEndOffset + inlinersRelativeOffset);
                uint sameModuleCount = nibbleReader.ReadUInt();

                var inlinerRids = new List<int>();
                int baseRid = 0;
                for (uint i = 0; i < sameModuleCount; i++)
                {
                    int currentRid = baseRid + (int)nibbleReader.ReadUInt();
                    inlinerRids.Add(currentRid);
                    baseRid = currentRid;
                }

                entries.Add(new RawInliningInfoEntry(inlineeRid, inlinerRids));
            }

            return new RawInliningInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the v1 InliningInfo table.
    /// </summary>
    public sealed class RawInliningInfoEntry
    {
        /// <summary>MethodDef RID of the inlinee.</summary>
        public int InlineeRid { get; }

        /// <summary>MethodDef RIDs of the methods that inline the inlinee.</summary>
        public IReadOnlyList<int> InlinerRids { get; }

        public RawInliningInfoEntry(int inlineeRid, List<int> inlinerRids)
        {
            InlineeRid = inlineeRid;
            InlinerRids = inlinerRids;
        }
    }
}
