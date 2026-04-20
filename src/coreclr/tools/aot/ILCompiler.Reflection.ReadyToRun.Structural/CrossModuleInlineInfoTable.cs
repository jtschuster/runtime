// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the CrossModuleInlineInfo section (section 119, v6.3+).
    /// A NativeHashtable of inlining entries with cross-module support.
    /// No method name resolution is performed.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>InliningInfoNode (InfoType.CrossModuleInliningForCrossModuleDataOnly or InfoType.CrossModuleAllMethods)</c>.
    /// </remarks>
    public sealed class CrossModuleInlineInfoTable
    {
        public IReadOnlyList<CrossModuleInlineEntry> Entries { get; }

        internal CrossModuleInlineInfoTable(List<CrossModuleInlineEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public CrossModuleInlineInfoTable GetCrossModuleInlineInfoTable(ReadyToRunSection section)
        {
            bool multiModuleFormat = (ReadyToRunHeader.Flags & (uint)ReadyToRunFlags.READYTORUN_FLAG_MultiModuleVersionBubble) != 0;

            int sectionOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_nativeReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_nativeReader, parser, (uint)(sectionOffset + section.Size));
            var enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<CrossModuleInlineEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                uint streamSize = curParser.GetUnsigned();
                uint inlineeIndexAndFlags = curParser.GetUnsigned();
                streamSize--;

                uint inlineeIndex = inlineeIndexAndFlags >> 2;
                bool hasCrossModuleInliners = (inlineeIndexAndFlags & 0x2) != 0;
                bool crossModuleInlinee = (inlineeIndexAndFlags & 0x1) != 0;

                uint inlineeModuleIndex = 0;
                if (!crossModuleInlinee && multiModuleFormat && streamSize > 0)
                {
                    inlineeModuleIndex = curParser.GetUnsigned();
                    streamSize--;
                }

                var inliners = new List<CrossModuleInlinerRef>();

                if (hasCrossModuleInliners && streamSize > 0)
                {
                    uint crossModuleInlinerCount = curParser.GetUnsigned();
                    streamSize--;

                    for (uint i = 0; i < crossModuleInlinerCount && streamSize > 0; i++)
                    {
                        uint inlinerIndex = curParser.GetUnsigned();
                        streamSize--;
                        inliners.Add(new CrossModuleInlinerRef(isCrossModule: true, index: inlinerIndex, moduleIndex: 0));
                    }
                }

                uint currentRid = 0;
                while (streamSize > 0)
                {
                    uint inlinerDeltaAndFlag = curParser.GetUnsigned();
                    streamSize--;

                    uint moduleIndex = inlineeModuleIndex;
                    if (multiModuleFormat)
                    {
                        currentRid += inlinerDeltaAndFlag >> 1;
                        if ((inlinerDeltaAndFlag & 0x1) != 0 && streamSize > 0)
                        {
                            moduleIndex = curParser.GetUnsigned();
                            streamSize--;
                        }
                    }
                    else
                    {
                        currentRid += inlinerDeltaAndFlag;
                    }
                    inliners.Add(new CrossModuleInlinerRef(isCrossModule: false, index: currentRid, moduleIndex: moduleIndex));
                }

                entries.Add(new CrossModuleInlineEntry(
                    crossModuleInlinee, inlineeIndex, inlineeModuleIndex, inliners));
                curParser = enumerator.GetNext();
            }

            return new CrossModuleInlineInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the CrossModuleInlineInfo table.
    /// </summary>
    public sealed class CrossModuleInlineEntry
    {
        /// <summary>Whether the inlinee is a cross-module reference (ILBody import index).</summary>
        public bool IsCrossModuleInlinee { get; }

        /// <summary>
        /// The inlinee index. If <see cref="IsCrossModuleInlinee"/> is true, this is an
        /// ILBody import section index. Otherwise, it's a MethodDef RID.
        /// </summary>
        public uint InlineeIndex { get; }

        /// <summary>Module index for local inlinees (0 = owner module).</summary>
        public uint InlineeModuleIndex { get; }

        /// <summary>List of inliner references.</summary>
        public IReadOnlyList<CrossModuleInlinerRef> Inliners { get; }

        public CrossModuleInlineEntry(bool isCrossModuleInlinee, uint inlineeIndex, uint inlineeModuleIndex, List<CrossModuleInlinerRef> inliners)
        {
            IsCrossModuleInlinee = isCrossModuleInlinee;
            InlineeIndex = inlineeIndex;
            InlineeModuleIndex = inlineeModuleIndex;
            Inliners = inliners;
        }
    }

    /// <summary>
    /// A reference to an inliner method in the CrossModuleInlineInfo format.
    /// </summary>
    public sealed class CrossModuleInlinerRef
    {
        /// <summary>Whether this is a cross-module reference (ILBody import index).</summary>
        public bool IsCrossModule { get; }

        /// <summary>
        /// The method index. If <see cref="IsCrossModule"/> is true, this is an ILBody
        /// import section index. Otherwise, it's a MethodDef RID.
        /// </summary>
        public uint Index { get; }

        /// <summary>Module index for local inliners.</summary>
        public uint ModuleIndex { get; }

        public CrossModuleInlinerRef(bool isCrossModule, uint index, uint moduleIndex)
        {
            IsCrossModule = isCrossModule;
            Index = index;
            ModuleIndex = moduleIndex;
        }
    }
}
