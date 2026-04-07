// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the EnclosingTypeMap section.
    /// Maps each TypeDef (1-based RID) to the RID of its enclosing type (0 if not nested).
    /// </summary>
    public sealed class EnclosingTypeMapTable
    {
        /// <summary>Number of TypeDef entries in the map.</summary>
        public int Count { get; }

        private readonly ushort[] _enclosingTypeRids;

        private EnclosingTypeMapTable(int count, ushort[] enclosingTypeRids)
        {
            Count = count;
            _enclosingTypeRids = enclosingTypeRids;
        }

        /// <summary>
        /// Returns the enclosing type RID for the TypeDef at the given 1-based RID.
        /// Returns 0 if the type is not nested.
        /// </summary>
        public int GetEnclosingTypeRid(int typeDefRid)
        {
            if (typeDefRid < 1 || typeDefRid > Count)
                return 0;

            return _enclosingTypeRids[typeDefRid - 1];
        }

        public static EnclosingTypeMapTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            ushort count = reader.ImageReader.ReadUInt16(ref offset);
            ushort[] rids = new ushort[count];

            for (int i = 0; i < count; i++)
                rids[i] = reader.ImageReader.ReadUInt16(ref offset);

            return new EnclosingTypeMapTable(count, rids);
        }
    }
}
