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
    public sealed class InliningInfoTable
    {
        public IReadOnlyList<InliningInfoEntry> Entries { get; }

        internal InliningInfoTable(List<InliningInfoEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public InliningInfoTable GetInliningInfoTable(ReadyToRunSection section)
        {
            int startOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int offset = startOffset;
            int sizeOfInlineIndex = _nativeReader.ReadInt32(ref offset);
            int inlineIndexEndOffset = offset + sizeOfInlineIndex;
            var entries = new List<InliningInfoEntry>();

            while (offset < inlineIndexEndOffset)
            {
                int inlineeRid = _nativeReader.ReadInt32(ref offset);
                int inlinersRelativeOffset = _nativeReader.ReadInt32(ref offset);

                var nibbleReader = new NibbleReader(_nativeReader, inlineIndexEndOffset + inlinersRelativeOffset);
                uint sameModuleCount = nibbleReader.ReadUInt();

                var inlinerRids = new List<int>();
                int baseRid = 0;
                for (uint i = 0; i < sameModuleCount; i++)
                {
                    int currentRid = baseRid + (int)nibbleReader.ReadUInt();
                    inlinerRids.Add(currentRid);
                    baseRid = currentRid;
                }

                entries.Add(new InliningInfoEntry(inlineeRid, inlinerRids));
            }

            return new InliningInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the v1 InliningInfo table.
    /// </summary>
    public sealed class InliningInfoEntry
    {
        /// <summary>MethodDef RID of the inlinee.</summary>
        public int InlineeRid { get; }

        /// <summary>MethodDef RIDs of the methods that inline the inlinee.</summary>
        public IReadOnlyList<int> InlinerRids { get; }

        public InliningInfoEntry(int inlineeRid, List<int> inlinerRids)
        {
            InlineeRid = inlineeRid;
            InlinerRids = inlinerRids;
        }
    }
}
