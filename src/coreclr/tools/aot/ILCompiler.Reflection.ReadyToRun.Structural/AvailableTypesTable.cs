// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the AvailableTypes section.
    /// A NativeHashtable of (rid, isExported) entries per available type.
    /// No type name resolution is performed.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>TypesTableNode</c>.
    /// </remarks>
    public sealed class AvailableTypesTable
    {
        public IReadOnlyList<AvailableTypeEntry> Entries { get; }

        internal AvailableTypesTable(List<AvailableTypeEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public AvailableTypesTable GetAvailableTypesTable(ReadyToRunSection section)
        {
            int sectionOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_nativeReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_nativeReader, parser, (uint)(sectionOffset + section.Size));
            NativeHashtable.AllEntriesEnumerator enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<AvailableTypeEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                uint rid = curParser.GetUnsigned();
                bool isExportedType = (rid & 1) != 0;
                rid >>= 1;
                entries.Add(new AvailableTypeEntry(rid, isExportedType));
                curParser = enumerator.GetNext();
            }

            return new AvailableTypesTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the AvailableTypes table.
    /// </summary>
    public sealed class AvailableTypeEntry
    {
        /// <summary>
        /// The row ID of the type. This is a TypeDef RID if <see cref="IsExportedType"/>
        /// is false, or an ExportedType RID if true.
        /// </summary>
        public uint Rid { get; }

        /// <summary>Whether this entry refers to an ExportedType (true) or TypeDef (false).</summary>
        public bool IsExportedType { get; }

        public AvailableTypeEntry(uint rid, bool isExportedType)
        {
            Rid = rid;
            IsExportedType = isExportedType;
        }

        public EntityHandle GetMetadataToken()
        {
            return IsExportedType ? MetadataTokens.ExportedTypeHandle((int)Rid) : MetadataTokens.TypeDefinitionHandle((int)Rid);
        }
    }
}
