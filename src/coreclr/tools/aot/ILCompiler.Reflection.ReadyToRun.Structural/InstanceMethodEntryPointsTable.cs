// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun
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
            int sectionOffset = GetOffsetForRVA(section.RelativeVirtualAddress);
            NativeParser parser = new NativeParser(_nativeReader, (uint)sectionOffset);
            NativeHashtable hashtable = new NativeHashtable(_nativeReader, parser, (uint)(sectionOffset + section.Size));
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

        /// <summary>
        /// Fully parse an <see cref="InstanceMethodEntry"/>: decode the method signature,
        /// followed by the inline runtime-function-index and fixup cells. The payload
        /// layout is method-signature || DecodeUnsigned(id) || optional back-reference || nibble-encoded fixups.
        /// </summary>
        public InstanceMethodPayload GetInstanceMethodPayload(InstanceMethodEntry entry)
        {
            R2RSignature signature = RawSignatureDecoder.DecodeMethodSignature(_nativeReader, entry.SignatureBlobOffset, TargetPointerSize);
            MethodSignature method = MethodSignature.FromSignature(signature);

            int offset = signature.EndOffset;
            (RuntimeFunctionIndex runtimeFunctionIndex, List<FixupCellRef> fixupCells) = DecodeRuntimeFunctionIdAndFixupCells(offset);
            return new InstanceMethodPayload(method, runtimeFunctionIndex, fixupCells);
        }

        /// <summary>
        /// Shared decode for MethodDefEntry/InstanceMethodEntry payload tail:
        /// compressed "id" (bit 0 = has-fixups, bit 1 = back-reference), followed by
        /// optional nibble-encoded fixup cell list.
        /// </summary>
        internal (RuntimeFunctionIndex, List<FixupCellRef>) DecodeRuntimeFunctionIdAndFixupCells(int offset)
        {
            uint id = 0;
            offset = (int)_nativeReader.DecodeUnsigned((uint)offset, ref id);

            int? fixupOffset = null;
            RuntimeFunctionIndex runtimeFunctionIndex;

            if ((id & 1) != 0)
            {
                if ((id & 2) != 0)
                {
                    uint val = 0;
                    _nativeReader.DecodeUnsigned((uint)offset, ref val);
                    offset -= (int)val;
                }

                fixupOffset = offset;
                runtimeFunctionIndex = (RuntimeFunctionIndex)(id >> 2);
            }
            else
            {
                runtimeFunctionIndex = (RuntimeFunctionIndex)(id >> 1);
            }

            var fixupCells = new List<FixupCellRef>();
            if (fixupOffset.HasValue)
            {
                NibbleReader nibbleReader = new NibbleReader(_nativeReader, fixupOffset.Value);
                uint curTableIndex = nibbleReader.ReadUInt();

                while (true)
                {
                    uint cellIndex = nibbleReader.ReadUInt();

                    while (true)
                    {
                        fixupCells.Add(new FixupCellRef(curTableIndex, cellIndex));

                        uint delta = nibbleReader.ReadUInt();
                        if (delta == 0)
                            break;

                        cellIndex += delta;
                    }

                    uint tableDelta = nibbleReader.ReadUInt();
                    if (tableDelta == 0)
                        break;

                    curTableIndex += tableDelta;
                }
            }

            return (runtimeFunctionIndex, fixupCells);
        }
    }

    /// <summary>
    /// Fully decoded payload for an <see cref="InstanceMethodEntry"/>:
    /// the method reference, the runtime function index of its entry point,
    /// and any fixup cell references.
    /// </summary>
    public sealed class InstanceMethodPayload
    {
        public MethodSignature Method { get; }
        public RuntimeFunctionIndex EntryPointIndex { get; }
        public IReadOnlyList<FixupCellRef> FixupCells { get; }

        public InstanceMethodPayload(MethodSignature method, RuntimeFunctionIndex entryPointIndex, IReadOnlyList<FixupCellRef> fixupCells)
        {
            Method = method;
            EntryPointIndex = entryPointIndex;
            FixupCells = fixupCells;
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
