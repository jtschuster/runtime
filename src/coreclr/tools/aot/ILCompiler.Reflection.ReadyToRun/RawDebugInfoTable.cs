// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the DebugInfo section.
    /// A NativeSparseArray mapping runtime function IDs to debug data offsets.
    /// </summary>
    public sealed class RawDebugInfoTable
    {
        public IReadOnlyList<RawDebugInfoEntry> Entries { get; }

        private RawDebugInfoTable(List<RawDebugInfoEntry> entries)
        {
            Entries = entries;
        }

        public static RawDebugInfoTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeSparseArray debugInfoArray = new NativeSparseArray(reader.ImageReader, (uint)sectionOffset);
            uint count = debugInfoArray.GetCount();
            var entries = new List<RawDebugInfoEntry>();

            for (uint i = 0; i < count; i++)
            {
                int offset = 0;
                if (debugInfoArray.TryGetAt(i, ref offset))
                {
                    entries.Add(new RawDebugInfoEntry(i, offset));
                }
            }

            return new RawDebugInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the DebugInfo table: maps a runtime function index
    /// to the file offset of its debug data.
    /// </summary>
    public sealed class RawDebugInfoEntry
    {
        /// <summary>Runtime function index.</summary>
        public uint RuntimeFunctionIndex { get; }

        /// <summary>File offset of the debug info data.</summary>
        public int DebugInfoOffset { get; }

        public RawDebugInfoEntry(uint runtimeFunctionIndex, int debugInfoOffset)
        {
            RuntimeFunctionIndex = runtimeFunctionIndex;
            DebugInfoOffset = debugInfoOffset;
        }
    }
}
