// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the HotColdMap section.
    /// Each entry pairs a hot runtime function index with the first cold
    /// runtime function index for the same method.
    /// </summary>
    public sealed class HotColdMapTable
    {
        public IReadOnlyList<HotColdMapEntry> Entries { get; }

        private HotColdMapTable(List<HotColdMapEntry> entries)
        {
            Entries = entries;
        }

        public static HotColdMapTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            int count = section.Size / (2 * sizeof(int));
            var entries = new List<HotColdMapEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int coldRuntimeFunctionIndex = reader.ImageReader.ReadInt32(ref offset);
                int hotRuntimeFunctionIndex = reader.ImageReader.ReadInt32(ref offset);
                entries.Add(new HotColdMapEntry(hotRuntimeFunctionIndex, coldRuntimeFunctionIndex));
            }

            return new HotColdMapTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the HotColdMap: maps a hot runtime function index
    /// to a cold runtime function index.
    /// </summary>
    public sealed class HotColdMapEntry
    {
        /// <summary>Index of the hot runtime function.</summary>
        public int HotRuntimeFunctionIndex { get; }

        /// <summary>Index of the cold runtime function.</summary>
        public int ColdRuntimeFunctionIndex { get; }

        public HotColdMapEntry(int hotRuntimeFunctionIndex, int coldRuntimeFunctionIndex)
        {
            HotRuntimeFunctionIndex = hotRuntimeFunctionIndex;
            ColdRuntimeFunctionIndex = coldRuntimeFunctionIndex;
        }
    }
}
