// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the TypeGenericInfoMap section.
    /// Packed 4-bit entries, one per TypeDef, encoding generic parameter count,
    /// constraints, and variance info.
    /// </summary>
    public sealed class TypeGenericInfoMapTable
    {
        /// <summary>Number of TypeDef entries in the map.</summary>
        public int Count { get; }

        private readonly byte[] _data;

        internal TypeGenericInfoMapTable(int count, byte[] data)
        {
            Count = count;
            _data = data;
        }

        /// <summary>
        /// Returns the 4-bit generic info for the TypeDef at the given 1-based RID.
        /// The bits encode:
        ///   [1:0] generic count (0, 1, 2, MoreThanTwo)
        ///   [2]   HasConstraints
        ///   [3]   HasVariance
        /// </summary>
        public ReadyToRunTypeGenericInfo GetInfo(int typeDefRid)
        {
            if (typeDefRid < 1 || typeDefRid > Count)
                return 0;

            int index = typeDefRid - 1;
            int byteIndex = index / 2;
            byte nibble = (index % 2 == 0)
                ? (byte)((_data[byteIndex] >> 4) & 0x0F)
                : (byte)(_data[byteIndex] & 0x0F);

            return (ReadyToRunTypeGenericInfo)nibble;
        }
    }

    public partial class ReadyToRunReader
    {
        public TypeGenericInfoMapTable GetTypeGenericInfoMapTable(ReadyToRunSectionHandle section)
        {
            int offset = GetOffset(section.RelativeVirtualAddress);
            int count = _imageReader.ReadInt32(ref offset);
            int byteCount = (count + 1) / 2;
            byte[] data = new byte[byteCount];

            for (int i = 0; i < byteCount; i++)
                data[i] = _imageReader.ReadByte(ref offset);

            return new TypeGenericInfoMapTable(count, data);
        }
    }
}
