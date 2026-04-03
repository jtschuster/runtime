// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the PgoInstrumentationData section.
    /// A NativeHashtable where each entry contains a signature blob offset
    /// and a PGO data blob offset. No signature decoding is performed.
    /// </summary>
    public sealed class RawPgoInstrumentationDataTable
    {
        public IReadOnlyList<RawPgoEntry> Entries { get; }

        private RawPgoInstrumentationDataTable(List<RawPgoEntry> entries)
        {
            Entries = entries;
        }

        public static RawPgoInstrumentationDataTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int sectionOffset = reader.GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(reader.ImageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)(sectionOffset + section.Size));
            var enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<RawPgoEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int signatureBlobOffset = (int)curParser.Offset;
                byte lowHashcode = curParser.LowHashcode;

                // Like InstanceMethodEntryPoints, the signature blob must be
                // decoded by a higher-level reader. We store the blob offset only.
                entries.Add(new RawPgoEntry(signatureBlobOffset, lowHashcode));
                curParser = enumerator.GetNext();
            }

            return new RawPgoInstrumentationDataTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the PgoInstrumentationData hashtable.
    /// Contains the offset of the signature + PGO data blob.
    /// </summary>
    public sealed class RawPgoEntry
    {
        /// <summary>File offset of the method signature + PGO data blob.</summary>
        public int SignatureBlobOffset { get; }

        /// <summary>Low byte of the hash code used for hashtable bucketing.</summary>
        public byte LowHashcode { get; }

        public RawPgoEntry(int signatureBlobOffset, byte lowHashcode)
        {
            SignatureBlobOffset = signatureBlobOffset;
            LowHashcode = lowHashcode;
        }
    }
}
