// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using Internal.Text;

namespace ILCompiler.ObjectWriter
{
    internal enum WasmLinkingSymbolKind : byte
    {
        Function = 0,
        Data = 1,
        Global = 2,
        Tag = 4,
    }

    internal readonly struct WasmRelocation
    {
        public WasmRelocationKind Kind { get; }
        public ulong Offset { get; }
        public ulong Index { get; }
        public long Addend { get; }

        public WasmRelocation(WasmRelocationKind kind, ulong offset, ulong index, long addend)
        {
            if (!HasAddend(kind) && addend != 0)
            {
                throw new InvalidOperationException(
                    $"WebAssembly relocation {kind} cannot encode addend {addend}.");
            }

            Kind = kind;
            Offset = offset;
            Index = index;
            Addend = addend;
        }

        public static bool HasAddend(WasmRelocationKind kind) => kind is
            WasmRelocationKind.MemoryAddressLeb or
            WasmRelocationKind.MemoryAddressSleb or
            WasmRelocationKind.MemoryAddressI32 or
            WasmRelocationKind.FunctionOffsetI32 or
            WasmRelocationKind.SectionOffsetI32 or
            WasmRelocationKind.MemoryAddressRelativeSleb or
            WasmRelocationKind.MemoryAddressLeb64 or
            WasmRelocationKind.MemoryAddressSleb64 or
            WasmRelocationKind.MemoryAddressI64 or
            WasmRelocationKind.MemoryAddressRelativeSleb64 or
            WasmRelocationKind.MemoryAddressTlsSleb or
            WasmRelocationKind.FunctionOffsetI64 or
            WasmRelocationKind.MemoryAddressLocRelativeI32 or
            WasmRelocationKind.MemoryAddressTlsSleb64;
    }

    internal readonly struct WasmSegmentInfo
    {
        public Utf8String Name { get; }
        public int Alignment { get; }
        public uint Flags { get; }

        public WasmSegmentInfo(Utf8String name, int alignment, uint flags)
        {
            Name = name;
            Alignment = alignment;
            Flags = flags;
        }
    }

    internal readonly struct WasmLinkingSymbol
    {
        public WasmLinkingSymbolKind Kind { get; }
        public uint Flags { get; }
        public Utf8String Name { get; }
        public int Index { get; }
        public bool IsDefined { get; }
        public int SegmentIndex { get; }
        public long Offset { get; }
        public int Size { get; }

        public WasmLinkingSymbol(
            WasmLinkingSymbolKind kind,
            uint flags,
            Utf8String name,
            int index,
            bool isDefined,
            int segmentIndex,
            long offset,
            int size)
        {
            Kind = kind;
            Flags = flags;
            Name = name;
            Index = index;
            IsDefined = isDefined;
            SegmentIndex = segmentIndex;
            Offset = offset;
            Size = size;
        }
    }

    internal readonly struct WasmFunctionName
    {
        public int Index { get; }
        public Utf8String Name { get; }

        public WasmFunctionName(int index, Utf8String name)
        {
            Index = index;
            Name = name;
        }
    }

    internal abstract class WasmVectorSubsection<TEntry> : IWasmEmittable
    {
        private readonly Stream _contentReadStream;

        protected WasmVectorSubsection(byte id, Stream contentReadStream, int sectionIndex)
        {
            Id = id;
            _contentReadStream = contentReadStream;
            SectionIndex = sectionIndex;
        }

        public byte Id { get; }
        public int EntryCount { get; private set; }
        public int SectionIndex { get; }

        private int ContentPrefixSize => (int)DwarfHelper.SizeOfULEB128((ulong)EntryCount);

        private int ContentSize => ContentPrefixSize + checked((int)_contentReadStream.Length);

        public int EncodedSize() =>
            sizeof(byte) +
            (int)DwarfHelper.SizeOfULEB128((ulong)ContentSize) +
            ContentSize;

        public int EmitToStream(Stream outputFileStream)
        {
            outputFileStream.WriteByte(Id);
            WriteULEB128(outputFileStream, checked((ulong)ContentSize));
            WriteULEB128(outputFileStream, checked((ulong)EntryCount));
            _contentReadStream.Position = 0;
            _contentReadStream.CopyTo(outputFileStream);
            return EncodedSize();
        }

        public void WriteEntry(SectionWriter writer, TEntry entry)
        {
            Debug.Assert(writer.SectionIndex == SectionIndex);
            WriteEntryCore(writer, entry);
            EntryCount++;
        }

        protected abstract void WriteEntryCore(SectionWriter writer, TEntry entry);

        private static void WriteULEB128(Stream output, ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            int bytesWritten = DwarfHelper.WriteULEB128(buffer, value);
            output.Write(buffer.Slice(0, bytesWritten));
        }
    }

    internal sealed class WasmSegmentInfoSubsection : WasmVectorSubsection<WasmSegmentInfo>
    {
        public WasmSegmentInfoSubsection(byte id, Stream stream, int sectionIndex)
            : base(id, stream, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmSegmentInfo entry)
        {
            writer.WriteUtf8WithLength(entry.Name);
            writer.WriteULEB128(checked((ulong)entry.Alignment));
            writer.WriteULEB128(entry.Flags);
        }
    }

    internal sealed class WasmSymbolTableSubsection : WasmVectorSubsection<WasmLinkingSymbol>
    {
        public WasmSymbolTableSubsection(byte id, Stream stream, int sectionIndex)
            : base(id, stream, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmLinkingSymbol entry)
        {
            writer.WriteByte((byte)entry.Kind);
            writer.WriteULEB128(entry.Flags);

            switch (entry.Kind)
            {
                case WasmLinkingSymbolKind.Function:
                    writer.WriteULEB128(checked((ulong)entry.Index));
                    if (entry.IsDefined)
                    {
                        writer.WriteUtf8WithLength(entry.Name);
                    }
                    break;

                case WasmLinkingSymbolKind.Data:
                    writer.WriteUtf8WithLength(entry.Name);
                    if (entry.IsDefined)
                    {
                        writer.WriteULEB128(checked((ulong)entry.SegmentIndex));
                        writer.WriteULEB128(checked((ulong)entry.Offset));
                        writer.WriteULEB128(checked((ulong)entry.Size));
                    }
                    break;

                case WasmLinkingSymbolKind.Global:
                case WasmLinkingSymbolKind.Tag:
                    writer.WriteULEB128(checked((ulong)entry.Index));
                    if (entry.IsDefined)
                    {
                        writer.WriteUtf8WithLength(entry.Name);
                    }
                    break;

                default:
                    throw new UnreachableException();
            }
        }
    }

    internal sealed class WasmFunctionNamesSubsection : WasmVectorSubsection<WasmFunctionName>
    {
        public WasmFunctionNamesSubsection(byte id, Stream stream, int sectionIndex)
            : base(id, stream, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmFunctionName entry)
        {
            writer.WriteULEB128(checked((ulong)entry.Index));
            writer.WriteUtf8WithLength(entry.Name);
        }
    }

    internal sealed class WasmLinkingSection : WasmCustomSection
    {
        private const uint LinkingVersion = 2;

        protected override int CustomPayloadPrefixSize =>
            (int)DwarfHelper.SizeOfULEB128(LinkingVersion);

        protected override int EncodeCustomPayloadPrefix(Span<byte> destination) =>
            DwarfHelper.WriteULEB128(destination, LinkingVersion);

        public WasmLinkingSection(Stream stream, Utf8String name, int sectionIndex)
            : base(stream, name, sectionIndex)
        {
        }
    }

    internal sealed class WasmNameSection : WasmCustomSection
    {
        public WasmNameSection(Stream stream, int sectionIndex)
            : base(stream, new Utf8String("name"), sectionIndex)
        {
        }
    }

    internal sealed class WasmRelocationSection : WasmCustomSection
    {
        private readonly uint _targetSectionIndex;

        public WasmRelocationSection(
            Stream stream,
            Utf8String name,
            int sectionIndex,
            uint targetSectionIndex)
            : base(stream, name, sectionIndex)
        {
            _targetSectionIndex = targetSectionIndex;
        }

        public int EntryCount { get; private set; }

        protected override int CustomPayloadPrefixSize =>
            (int)DwarfHelper.SizeOfULEB128(_targetSectionIndex) +
            (int)DwarfHelper.SizeOfULEB128((ulong)EntryCount);

        protected override int EncodeCustomPayloadPrefix(Span<byte> destination)
        {
            int bytesWritten = DwarfHelper.WriteULEB128(destination, _targetSectionIndex);
            return bytesWritten +
                DwarfHelper.WriteULEB128(destination.Slice(bytesWritten), checked((ulong)EntryCount));
        }

        public void WriteEntry(SectionWriter writer, WasmRelocation entry)
        {
            Debug.Assert(writer.SectionIndex == SectionIndex);

            writer.WriteByte((byte)entry.Kind);
            writer.WriteULEB128(entry.Offset);
            writer.WriteULEB128(entry.Index);
            if (WasmRelocation.HasAddend(entry.Kind))
            {
                writer.WriteSLEB128(entry.Addend);
            }

            EntryCount++;
        }
    }
}
