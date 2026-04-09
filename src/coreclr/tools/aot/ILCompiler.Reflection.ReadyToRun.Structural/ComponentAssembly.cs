// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structure representing an element of the assembly table in composite R2R images.
    /// </summary>
    public class ComponentAssembly
    {
        public const int Size = 4 * sizeof(int);

        public readonly int CorHeaderRVA;
        public readonly int CorHeaderSize;
        public readonly int AssemblyHeaderRVA;
        public readonly int AssemblyHeaderSize;

        public ComponentAssembly(NativeReader imageReader, ref int curOffset)
        {
            CorHeaderRVA = imageReader.ReadInt32(ref curOffset);
            CorHeaderSize = imageReader.ReadInt32(ref curOffset);
            AssemblyHeaderRVA = imageReader.ReadInt32(ref curOffset);
            AssemblyHeaderSize = imageReader.ReadInt32(ref curOffset);
        }
    }
}
