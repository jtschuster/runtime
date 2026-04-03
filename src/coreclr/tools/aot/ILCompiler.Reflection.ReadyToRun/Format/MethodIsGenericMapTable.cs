// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.Reflection.ReadyToRun.Format
{
    /// <summary>
    /// Structural projection of the MethodIsGenericMap section.
    /// A bitvector where each bit indicates whether the corresponding
    /// MethodDef (1-based RID) has generic parameters.
    /// </summary>
    public sealed class MethodIsGenericMapTable
    {
        /// <summary>Total number of method entries in the map.</summary>
        public int Count { get; }

        private readonly byte[] _data;

        private MethodIsGenericMapTable(int count, byte[] data)
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
            return (_data[byteIndex] & (1 << bitIndex)) != 0;
        }

        public static MethodIsGenericMapTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            int count = reader.ImageReader.ReadInt32(ref offset);
            int byteCount = (count + 7) / 8;
            byte[] data = new byte[byteCount];

            for (int i = 0; i < byteCount; i++)
                data[i] = reader.ImageReader.ReadByte(ref offset);

            return new MethodIsGenericMapTable(count, data);
        }
    }
}
