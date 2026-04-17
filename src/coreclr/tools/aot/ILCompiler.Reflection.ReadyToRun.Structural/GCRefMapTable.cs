// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun;

/// <summary>
/// Token types for GC reference map entries.
/// </summary>
public enum GCRefMapToken
{
    Skip = 0,
    Ref = 1,
    Interior = 2,
    MethodParam = 3,
    TypeParam = 4,
    VaSigCookie = 5,
}

/// <summary>
/// A single entry in a GC reference map, recording a raw position and its GC token.
/// The position is a raw slot index; architecture-aware translation to stack frame
/// offsets is left to higher-level consumers.
/// </summary>
public readonly struct GCRefMapEntry
{
    /// <summary>Raw position in the GC ref map (slot index, not byte offset).</summary>
    public int Position { get; }

    /// <summary>GC reference type at this position.</summary>
    public GCRefMapToken Token { get; }

    public GCRefMapEntry(int position, GCRefMapToken token)
    {
        Position = position;
        Token = token;
    }
}

/// <summary>
/// A decoded GC reference map for a single import slot.
/// </summary>
public sealed class GCRefMap
{
    /// <summary>
    /// Stack pop count (x86 only). <see cref="InvalidStackPop"/> if not applicable.
    /// </summary>
    public uint StackPop { get; }

    /// <summary>GC reference entries for this slot.</summary>
    public IReadOnlyList<GCRefMapEntry> Entries { get; }

    public const uint InvalidStackPop = ~0u;

    internal GCRefMap(uint stackPop, IReadOnlyList<GCRefMapEntry> entries)
    {
        StackPop = stackPop;
        Entries = entries;
    }
}

/// <summary>
/// A table of GC reference maps decoded from the auxiliary data of an import section.
/// Contains one <see cref="GCRefMap"/> per slot in the owning import section.
/// Obtained via <see cref="ReadyToRunReader.GetGCRefMapTable(AuxiliaryDataTableHandle, int, Machine)"/>.
/// </summary>
public sealed class GCRefMapTable : IEnumerable<GCRefMap>
{
    private readonly IReadOnlyList<GCRefMap> _entries;

    internal GCRefMapTable(IReadOnlyList<GCRefMap> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    public IEnumerator<GCRefMap> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public partial class ReadyToRunReader
{
    private Dictionary<AuxiliaryDataTableHandle, GCRefMapTable> _gcRefMapTableCache;

    /// <summary>
    /// Decode the GC reference map table at the given auxiliary data handle.
    /// </summary>
    /// <param name="handle">Handle from <see cref="ImportSectionEntry.AuxiliaryDataRva"/>.</param>
    /// <param name="entryCount">Number of slots in the owning import section.</param>
    public GCRefMapTable GetGCRefMapTable(AuxiliaryDataTableHandle handle, int entryCount)
    {
        if ((int)handle == 0 || entryCount <= 0)
            return null;

        _gcRefMapTableCache ??= new Dictionary<AuxiliaryDataTableHandle, GCRefMapTable>();

        if (_gcRefMapTableCache.TryGetValue(handle, out GCRefMapTable cached))
            return cached;

        int auxDataOffset = GetOffsetForRVA((int)handle);
        var entries = new GCRefMap[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            int strideIndex = i / GCRefMapLookupStride;
            int remaining = i % GCRefMapLookupStride;

            int lookupOffset = auxDataOffset + sizeof(int) * strideIndex;
            int entryOffset = auxDataOffset + _nativeReader.ReadInt32(ref lookupOffset);

            // Skip forward through compressed records to reach the target slot
            while (remaining > 0)
            {
                byte b;
                do
                {
                    b = _nativeReader.ReadByte(ref entryOffset);
                }
                while ((b & 0x80) != 0);
                remaining--;
            }

            entries[i] = DecodeGCRefMap(entryOffset);
        }

        var table = new GCRefMapTable(entries);
        _gcRefMapTableCache[handle] = table;
        return table;
    }

    private const int GCRefMapLookupStride = 1024;

    private GCRefMap DecodeGCRefMap(int offset)
    {
        var decoder = new GCRefMapBitReader(_nativeReader, offset);

        uint stackPop = GCRefMap.InvalidStackPop;
        if (Machine == Machine.I386)
        {
            int x = decoder.GetTwoBit();
            if (x == 3)
                x = decoder.GetInt() + 3;
            stackPop = (uint)x;
        }

        var entries = new List<GCRefMapEntry>();
        int pos = 0;

        while (!decoder.AtEnd())
        {
            int currentPos = pos;
            GCRefMapToken token = ReadToken(ref pos, decoder);
            if (token != GCRefMapToken.Skip)
            {
                entries.Add(new GCRefMapEntry(currentPos, token));
            }
        }

        if (stackPop != GCRefMap.InvalidStackPop || entries.Count > 0)
            return new GCRefMap(stackPop, entries.ToArray());

        return null;
    }

    private static GCRefMapToken ReadToken(ref int pos, GCRefMapBitReader decoder)
    {
        int val = decoder.GetTwoBit();
        if (val == 3)
        {
            int ext = decoder.GetInt();
            if ((ext & 1) == 0)
            {
                pos += (ext >> 1) + 4;
                return GCRefMapToken.Skip;
            }
            else
            {
                pos++;
                return (GCRefMapToken)((ext >> 1) + 3);
            }
        }
        pos++;
        return (GCRefMapToken)val;
    }

    /// <summary>
    /// Bit-oriented reader for the GC ref map compressed format.
    /// </summary>
    private struct GCRefMapBitReader
    {
        private readonly NativeReader _reader;
        private int _offset;
        private int _pendingByte;

        public GCRefMapBitReader(NativeReader reader, int offset)
        {
            _reader = reader;
            _offset = offset;
            _pendingByte = 0x80;
        }

        public int GetBit()
        {
            int x = _pendingByte;
            if ((x & 0x80) != 0)
            {
                x = _reader.ReadByte(ref _offset);
                x |= (x & 0x80) << 7;
            }
            _pendingByte = x >> 1;
            return x & 1;
        }

        public int GetTwoBit()
        {
            int result = GetBit();
            result |= GetBit() << 1;
            return result;
        }

        public int GetInt()
        {
            int result = 0;
            int bit = 0;
            do
            {
                result |= GetBit() << (bit++);
                result |= GetBit() << (bit++);
                result |= GetBit() << (bit++);
            }
            while (GetBit() != 0);
            return result;
        }

        public bool AtEnd() => _pendingByte == 0;
    }
}
