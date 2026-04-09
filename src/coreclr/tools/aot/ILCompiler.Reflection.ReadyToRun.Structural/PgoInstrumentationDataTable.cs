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

        internal PgoInstrumentationDataTable(List<PgoEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public PgoInstrumentationDataTable GetPgoInstrumentationDataTable(ReadyToRunSectionHandle section)
        {
            int sectionOffset = GetOffset(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_imageReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_imageReader, parser, (uint)(sectionOffset + section.Size));
            var enumerator = hashtable.EnumerateAllEntries();
            var entries = new List<PgoEntry>();

            NativeParser curParser = enumerator.GetNext();
            while (!curParser.IsNull())
            {
                int signatureBlobOffset = (int)curParser.Offset;
                byte lowHashcode = curParser.LowHashcode;

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
