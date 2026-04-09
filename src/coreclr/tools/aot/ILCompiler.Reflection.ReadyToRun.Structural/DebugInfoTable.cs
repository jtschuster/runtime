// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the DebugInfo section.
    /// A NativeSparseArray mapping runtime function IDs to debug data offsets.
    /// </summary>
    public sealed class DebugInfoTable
    {
        public IReadOnlyList<DebugInfoEntry> Entries { get; }

        internal DebugInfoTable(List<DebugInfoEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public DebugInfoTable GetDebugInfoTable(ReadyToRunSectionHandle section)
        {
            int sectionOffset = GetOffset(section.RelativeVirtualAddress);
            NativeArray debugInfoArray = new NativeArray(_imageReader, (uint)sectionOffset);
            uint count = debugInfoArray.GetCount();
            var entries = new List<DebugInfoEntry>();

            for (uint i = 0; i < count; i++)
            {
                int offset = default;
                if (debugInfoArray.TryGetAt(i, ref offset))
                {
                    entries.Add(new DebugInfoEntry(i, (DebugInfoHandle)offset));
                }
            }

            return new DebugInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the DebugInfo table: maps a runtime function index
    /// to the file offset of its debug data.
    /// </summary>
    public sealed class DebugInfoEntry
    {
        /// <summary>Runtime function index.</summary>
        public uint RuntimeFunctionIndex { get; }

        /// <summary>File offset of the debug info data.</summary>
        public DebugInfoHandle DebugInfoOffset { get; }

        public DebugInfoEntry(uint runtimeFunctionIndex, DebugInfoHandle debugInfoOffset)
        {
            RuntimeFunctionIndex = runtimeFunctionIndex;
            DebugInfoOffset = debugInfoOffset;
        }
    }

    public enum DebugInfoHandle {}
}
