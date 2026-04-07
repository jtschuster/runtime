// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

using ILCompiler.Reflection.ReadyToRun;

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

        private InstanceMethodEntryPointsTable(List<InstanceMethodEntry> entries)
        {
            Entries = entries;
        }

        public static InstanceMethodEntryPointsTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(reader.ImageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)(sectionOffset + section.Size));
            NativeHashtable.AllEntriesEnumerator enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<InstanceMethodEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int signatureBlobOffset = (int)curParser.Offset;
                byte lowHashcode = curParser.LowHashcode;

                // We need to skip past the signature to find the entrypoint encoding.
                // The signature is a variable-length blob; we can't skip it without
                // partially decoding it. Instead, we store the blob offset and let
                // higher-level code decode it.
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
