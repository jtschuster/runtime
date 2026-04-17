// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Interface for abstracting binary image reading across different formats (PE, MachO).
    /// </summary>
    public interface IBinaryImageReader : IDisposable
    {
        /// <summary>
        /// Gets the machine type of the binary image
        /// </summary>
        Machine Machine { get; }

        /// <summary>
        /// Gets the operating system of the binary image
        /// </summary>
        OperatingSystem OperatingSystem { get; }

        /// <summary>
        /// Gets the image base address
        /// </summary>
        ulong ImageBase { get; }

        /// <summary>
        /// Get the index in the image byte array corresponding to the RVA
        /// </summary>
        /// <param name="rva">The relative virtual address</param>
        int GetOffset(int rva);

        /// <summary>
        /// Try to get the ReadyToRun header RVA for this image.
        /// </summary>
        /// <param name="rva">RVA of the ReadyToRun header if available, 0 when not</param>
        /// <param name="isComposite">true when the reader represents a composite ReadyToRun image, false for regular R2R</param>
        /// <returns>true when the reader represents a ReadyToRun image (composite or regular), false otherwise</returns>
        bool TryGetReadyToRunHeader(out int rva, out bool isComposite);

        /// <summary>
        /// Creates standalone assembly metadata from the image's embedded metadata.
        /// </summary>
        /// <remarks>
        /// The IBinaryImageReader object MUST not be disposed while the returned MetadataReader is still in use.
        /// The implementor of IBinaryImageReader MUST ensure that the returned MetadataReader remains valid until the implementor is disposed.
        /// Failure to do so may result in unsafe behavior.
        /// </remarks>
        /// <returns>Assembly metadata, or null if the image has no embedded metadata</returns>
        MetadataReader GetStandaloneAssemblyMetadata();

        /// <summary>
        /// Creates manifest assembly metadata from the R2R manifest section at the given file offset and size.
        /// </summary>
        /// <remarks>
        /// The IBinaryImageReader object MUST not be disposed while the returned MetadataReader is still in use.
        /// The implementor of IBinaryImageReader MUST ensure that the returned MetadataReader remains valid until the implementor is disposed.
        /// Failure to do so may result in unsafe behavior.
        /// </remarks>
        /// <param name="offset">File offset of the manifest metadata section.</param>
        /// <param name="size">Size of the manifest metadata section in bytes.</param>
        /// <returns>Manifest assembly metadata</returns>
        MetadataReader GetManifestAssemblyMetadata(int offset, int size);

        /// <summary>
        /// Write out image information using the specified writer
        /// </summary>
        /// <param name="writer">The writer to use</param>
        void DumpImageInformation(TextWriter writer);

        /// <summary>
        /// Gets the sections (name and size) of the binary image
        /// </summary>
        Dictionary<string, int> GetSections();

        // static abstract IBinaryImageReader Create(Stream stream);
    }
}
