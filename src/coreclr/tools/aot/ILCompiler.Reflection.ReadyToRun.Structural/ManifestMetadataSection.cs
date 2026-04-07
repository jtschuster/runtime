// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Raw location of the ManifestMetadata ECMA-335 blob within the R2R image.
    /// The actual metadata content can be read from the image at <see cref="FileOffset"/>
    /// with length <see cref="Size"/>.
    /// </summary>
    public sealed class ManifestMetadataSection
    {
        /// <summary>File offset of the metadata blob within the image.</summary>
        public int FileOffset { get; }

        /// <summary>Size of the metadata blob in bytes.</summary>
        public int Size { get; }

        public ManifestMetadataSection(int fileOffset, int size)
        {
            FileOffset = fileOffset;
            Size = size;
        }
    }
}
