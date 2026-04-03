// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the InliningInfo2 section (section 114, v4.1+).
    /// A NativeHashtable of inlining entries. Each entry maps an inlinee
    /// (identified by RID and optional module index) to a list of inliners.
    /// No method name resolution is performed.
    /// </summary>
    public sealed class RawInliningInfo2Table
    {
        public IReadOnlyList<RawInliningInfo2Entry> Entries { get; }

        private RawInliningInfo2Table(List<RawInliningInfo2Entry> entries)
        {
            Entries = entries;
        }

        public static RawInliningInfo2Table Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(reader.ImageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)(sectionOffset + section.Size));
            var enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<RawInliningInfo2Entry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int count = (int)curParser.GetUnsigned();
                int inlineeRidAndFlag = (int)curParser.GetUnsigned();
                count--;

                int inlineeRid = inlineeRidAndFlag >> 1;
                bool inlineeHasModule = (inlineeRidAndFlag & 1) != 0;
                uint inlineeModuleIndex = 0;

                if (inlineeHasModule)
                {
                    inlineeModuleIndex = curParser.GetUnsigned();
                    count--;
                }

                var inliners = new List<RawInlinerRef>();
                int currentRid = 0;

                while (count > 0)
                {
                    int inlinerDeltaAndFlag = (int)curParser.GetUnsigned();
                    count--;
                    int inlinerDelta = inlinerDeltaAndFlag >> 1;
                    currentRid += inlinerDelta;

                    bool inlinerHasModule = (inlinerDeltaAndFlag & 1) != 0;
                    uint inlinerModuleIndex = 0;

                    if (inlinerHasModule)
                    {
                        inlinerModuleIndex = curParser.GetUnsigned();
                        count--;
                    }

                    inliners.Add(new RawInlinerRef(currentRid, inlinerHasModule, inlinerModuleIndex));
                }

                entries.Add(new RawInliningInfo2Entry(inlineeRid, inlineeHasModule, inlineeModuleIndex, inliners));
                curParser = enumerator.GetNext();
            }

            return new RawInliningInfo2Table(entries);
        }
    }

    /// <summary>
    /// A single entry in the InliningInfo2 table.
    /// </summary>
    public sealed class RawInliningInfo2Entry
    {
        /// <summary>MethodDef RID of the inlinee.</summary>
        public int InlineeRid { get; }

        /// <summary>Whether the inlinee has a module index override.</summary>
        public bool InlineeHasModule { get; }

        /// <summary>Module index of the inlinee (0 = owner module).</summary>
        public uint InlineeModuleIndex { get; }

        /// <summary>List of inliner method references.</summary>
        public IReadOnlyList<RawInlinerRef> Inliners { get; }

        public RawInliningInfo2Entry(int inlineeRid, bool inlineeHasModule, uint inlineeModuleIndex, List<RawInlinerRef> inliners)
        {
            InlineeRid = inlineeRid;
            InlineeHasModule = inlineeHasModule;
            InlineeModuleIndex = inlineeModuleIndex;
            Inliners = inliners;
        }
    }

    /// <summary>
    /// A reference to an inliner method in the InliningInfo2 format.
    /// </summary>
    public sealed class RawInlinerRef
    {
        /// <summary>MethodDef RID of the inliner.</summary>
        public int Rid { get; }

        /// <summary>Whether this inliner has a module index override.</summary>
        public bool HasModule { get; }

        /// <summary>Module index of the inliner (0 = owner module).</summary>
        public uint ModuleIndex { get; }

        public RawInlinerRef(int rid, bool hasModule, uint moduleIndex)
        {
            Rid = rid;
            HasModule = hasModule;
            ModuleIndex = moduleIndex;
        }
    }
}
