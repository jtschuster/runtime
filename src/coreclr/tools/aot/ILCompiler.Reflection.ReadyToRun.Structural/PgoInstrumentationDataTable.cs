// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the PgoInstrumentationData section.
    /// A NativeHashtable where each entry points at a method-signature blob immediately
    /// followed by a versionAndFlags word and a (possibly back-referenced) PGO data blob.
    /// Use <see cref="ReadyToRunReader.GetPgoPayload(PgoEntry)"/> to fully decode an entry.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>InstrumentationDataTableNode</c>.
    /// </remarks>
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
        public PgoInstrumentationDataTable GetPgoInstrumentationDataTable(ReadyToRunSection section)
        {
            int sectionOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_nativeReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_nativeReader, parser, (uint)(sectionOffset + section.Size));
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

        /// <summary>
        /// Fully parse a <see cref="PgoEntry"/>: decode the method signature, then read
        /// the encoded versionAndFlags word and resolve the PGO data blob offset.
        /// </summary>
        /// <remarks>
        /// Payload layout at <see cref="PgoEntry.SignatureBlobOffset"/>:
        /// method-signature || DecodeUnsigned(versionAndFlags) || (optional back-reference) || pgo-data-blob.
        /// The low 2 bits of <c>versionAndFlags</c> are the tag:
        /// <list type="bullet">
        ///   <item><description><c>1</c> — PGO data blob is inline immediately after the versionAndFlags word.</description></item>
        ///   <item><description><c>3</c> — a second <c>DecodeUnsigned</c> follows; subtract that delta from the
        ///     post-versionAndFlags offset to find the deduplicated PGO data blob.</description></item>
        ///   <item><description>any other value — invalid PGO format.</description></item>
        /// </list>
        /// The remaining bits (<c>versionAndFlags &gt;&gt; 2</c>) are the PGO format version.
        /// The PGO data blob itself is a schema-driven sequence of compressed-int records;
        /// decoding it is intentionally left to higher-layer consumers.
        /// </remarks>
        public PgoPayload GetPgoPayload(PgoEntry entry)
        {
            R2RSignature signature = RawSignatureDecoder.DecodeMethodSignature(_nativeReader, entry.SignatureBlobOffset, TargetPointerSize);
            MethodSignature method = MethodSignature.FromSignature(signature);

            int offset = signature.EndOffset;
            uint versionAndFlags = 0;
            offset = (int)_nativeReader.DecodeUnsigned((uint)offset, ref versionAndFlags);

            int pgoDataBlobOffset;
            switch (versionAndFlags & 3)
            {
                case 1:
                    // Inline: data follows versionAndFlags directly.
                    pgoDataBlobOffset = offset;
                    break;
                case 3:
                    // Back-reference: subtract delta from post-versionAndFlags offset.
                    uint delta = 0;
                    _nativeReader.DecodeUnsigned((uint)offset, ref delta);
                    pgoDataBlobOffset = offset - (int)delta;
                    break;
                default:
                    throw new BadImageFormatException("Invalid PGO instrumentation data format");
            }

            int pgoFormatVersion = (int)(versionAndFlags >> 2);
            return new PgoPayload(method, pgoFormatVersion, pgoDataBlobOffset);
        }
    }

    /// <summary>
    /// A single entry in the PgoInstrumentationData hashtable.
    /// Holds only the signature blob offset and bucketing hash; use
    /// <see cref="ReadyToRunReader.GetPgoPayload(PgoEntry)"/> to decode the rest.
    /// </summary>
    public sealed class PgoEntry
    {
        /// <summary>File offset of the method signature blob (the entry's payload start).</summary>
        public int SignatureBlobOffset { get; }

        /// <summary>Low byte of the hash code used for hashtable bucketing.</summary>
        public byte LowHashcode { get; }

        public PgoEntry(int signatureBlobOffset, byte lowHashcode)
        {
            SignatureBlobOffset = signatureBlobOffset;
            LowHashcode = lowHashcode;
        }
    }

    /// <summary>
    /// Decoded payload for a <see cref="PgoEntry"/>.
    /// </summary>
    public sealed class PgoPayload
    {
        /// <summary>The method whose PGO data this entry holds.</summary>
        public MethodSignature Method { get; }

        /// <summary>PGO format version (the high bits of the versionAndFlags word).</summary>
        public int PgoFormatVersion { get; }

        /// <summary>
        /// File offset of the PGO data blob. For back-referenced entries this points at a
        /// previously-emitted (deduplicated) blob and may precede the entry's own offset.
        /// </summary>
        public int PgoDataBlobOffset { get; }

        public PgoPayload(MethodSignature method, int pgoFormatVersion, int pgoDataBlobOffset)
        {
            Method = method;
            PgoFormatVersion = pgoFormatVersion;
            PgoDataBlobOffset = pgoDataBlobOffset;
        }
    }
}
