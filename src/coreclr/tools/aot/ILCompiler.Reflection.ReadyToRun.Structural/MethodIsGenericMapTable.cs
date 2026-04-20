// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the MethodIsGenericMap section.
    /// A bitvector where each bit indicates whether the corresponding
    /// MethodDef (1-based RID) has generic parameters.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>MethodIsGenericMapNode</c>.
    /// </remarks>
    public sealed class MethodIsGenericMapTable
    {
        /// <summary>Total number of method entries in the map.</summary>
        public int Count { get; }

        private readonly byte[] _data;

        internal MethodIsGenericMapTable(int count, byte[] data)
        {
            Count = count;
            _data = data;
        }

        /// <summary>
        /// Returns whether the MethodDef at the given 1-based RID is generic.
        /// </summary>
        public bool IsGeneric(int rid)
        {
            if (rid < 1 || rid > Count)
                return false;

            int index = rid - 1;
            int byteIndex = index / 8;
            int bitIndex = index % 8;
            return (_data[byteIndex] & (1 << (7 - bitIndex))) != 0;
        }
    }

    public partial class ReadyToRunReader
    {
        public MethodIsGenericMapTable GetMethodIsGenericMapTable(ReadyToRunSection section)
        {
            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int count = _nativeReader.ReadInt32(ref offset);
            int byteCount = (count + 7) / 8;
            byte[] data = new byte[byteCount];

            for (int i = 0; i < byteCount; i++)
                data[i] = _nativeReader.ReadByte(ref offset);

            return new MethodIsGenericMapTable(count, data);
        }
    }
}
