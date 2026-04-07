// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the PgoInstrumentationData section.
    /// A NativeHashtable where each entry contains a signature blob offset
    /// and a PGO data blob offset. No signature decoding is performed.
    /// </summary>
    public sealed class PgoInstrumentationDataTable
    {
        public IReadOnlyList<PgoEntry> Entries { get; }

        private PgoInstrumentationDataTable(List<PgoEntry> entries)
        {
            Entries = entries;
        }

        public static PgoInstrumentationDataTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(reader.ImageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)(sectionOffset + section.Size));
            var enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<PgoEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int signatureBlobOffset = (int)curParser.Offset;
                byte lowHashcode = curParser.LowHashcode;

                // Like InstanceMethodEntryPoints, the signature blob must be
                // decoded by a higher-level reader. We store the blob offset only.
                entries.Add(new PgoEntry(signatureBlobOffset, lowHashcode));
                curParser = enumerator.GetNext();
            }

            return new PgoInstrumentationDataTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the PgoInstrumentationData hashtable.
    /// Contains the offset of the signature + PGO data blob.
    /// </summary>
    public sealed class PgoEntry
    {
        /// <summary>File offset of the method signature + PGO data blob.</summary>
        public int SignatureBlobOffset { get; }

        /// <summary>Low byte of the hash code used for hashtable bucketing.</summary>
        public byte LowHashcode { get; }

        public PgoEntry(int signatureBlobOffset, byte lowHashcode)
        {
            SignatureBlobOffset = signatureBlobOffset;
            LowHashcode = lowHashcode;
        }
    }
}
