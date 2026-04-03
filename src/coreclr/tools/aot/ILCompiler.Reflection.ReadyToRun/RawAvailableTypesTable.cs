// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the AvailableTypes section.
    /// A NativeHashtable of (rid, isExported) entries per available type.
    /// No type name resolution is performed.
    /// </summary>
    public sealed class RawAvailableTypesTable
    {
        public IReadOnlyList<RawAvailableTypeEntry> Entries { get; }

        private RawAvailableTypesTable(List<RawAvailableTypeEntry> entries)
        {
            Entries = entries;
        }

        internal static RawAvailableTypesTable FromEntries(List<RawAvailableTypeEntry> entries)
        {
            return new RawAvailableTypesTable(entries);
        }

        public static RawAvailableTypesTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(reader.ImageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)(sectionOffset + section.Size));
            NativeHashtable.AllEntriesEnumerator enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<RawAvailableTypeEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                uint rid = curParser.GetUnsigned();
                bool isExportedType = (rid & 1) != 0;
                rid >>= 1;
                entries.Add(new RawAvailableTypeEntry(rid, isExportedType));
                curParser = enumerator.GetNext();
            }

            return new RawAvailableTypesTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the AvailableTypes table.
    /// </summary>
    public sealed class RawAvailableTypeEntry
    {
        /// <summary>
        /// The row ID of the type. This is a TypeDef RID if <see cref="IsExportedType"/>
        /// is false, or an ExportedType RID if true.
        /// </summary>
        public uint Rid { get; }

        /// <summary>Whether this entry refers to an ExportedType (true) or TypeDef (false).</summary>
        public bool IsExportedType { get; }

        public RawAvailableTypeEntry(uint rid, bool isExportedType)
        {
            Rid = rid;
            IsExportedType = isExportedType;
        }
    }
}
