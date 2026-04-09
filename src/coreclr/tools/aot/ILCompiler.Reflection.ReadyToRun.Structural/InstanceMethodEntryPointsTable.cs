// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the InstanceMethodEntryPoints section.
    /// A NativeHashtable where each entry contains a signature blob offset,
    /// runtime function index, and optional fixup cells.
    /// No signature decoding is performed.
    /// </summary>
    public sealed class InstanceMethodEntryPointsTable
    {
        public IReadOnlyList<InstanceMethodEntry> Entries { get; }

        internal InstanceMethodEntryPointsTable(List<InstanceMethodEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public InstanceMethodEntryPointsTable GetInstanceMethodEntryPointsTable(ReadyToRunSectionHandle section)
        {
            int sectionOffset = GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_imageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_imageReader, parser, (uint)(sectionOffset + section.Size));
            NativeHashtable.AllEntriesEnumerator enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<InstanceMethodEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int signatureBlobOffset = (int)curParser.Offset;
                byte lowHashcode = curParser.LowHashcode;

                entries.Add(new InstanceMethodEntry(signatureBlobOffset, lowHashcode));
                curParser = enumerator.GetNext();
            }

            return new InstanceMethodEntryPointsTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the InstanceMethodEntryPoints hashtable.
    /// Contains the offset of the signature blob for this generic method instantiation.
    /// The signature must be decoded by a higher-level reader to extract the
    /// runtime function index and fixup cells.
    /// </summary>
    public sealed class InstanceMethodEntry
    {
        /// <summary>File offset of the method signature blob.</summary>
        public int SignatureBlobOffset { get; }

        /// <summary>Low byte of the hash code used for hashtable bucketing.</summary>
        public byte LowHashcode { get; }

        public InstanceMethodEntry(int signatureBlobOffset, byte lowHashcode)
        {
            SignatureBlobOffset = signatureBlobOffset;
            LowHashcode = lowHashcode;
        }
    }
}
