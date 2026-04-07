// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.Runtime;

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Represents the raw location and size of an R2R section whose internal
    /// format is either opaque, undocumented, or not yet parsed.
    /// </summary>
    public sealed class SectionData
    {
        /// <summary>The section type.</summary>
        public ReadyToRunSectionType Type { get; }

        /// <summary>Relative virtual address of the section.</summary>
        public int RelativeVirtualAddress { get; }

        /// <summary>Size of the section in bytes.</summary>
        public int Size { get; }

        /// <summary>File offset of the section within the image.</summary>
        public int FileOffset { get; }

        public SectionData(ReadyToRunSectionType type, int rva, int size, int fileOffset)
        {
            Type = type;
            RelativeVirtualAddress = rva;
            Size = size;
            FileOffset = fileOffset;
        }
    }
}
