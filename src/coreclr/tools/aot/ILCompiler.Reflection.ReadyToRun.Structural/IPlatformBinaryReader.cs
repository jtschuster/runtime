// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Minimal abstraction over a platform binary container (PE, Mach-O) that exposes
    /// just enough surface for the structural ReadyToRun reader to locate the R2R header
    /// and resolve RVA-based references.
    /// </summary>
    public interface IPlatformBinaryReader : IDisposable
    {
        /// <summary>
        /// Gets the machine type of the binary image.
        /// </summary>
        Machine Machine { get; }

        /// <summary>
        /// Translates a relative virtual address into a file offset within the underlying image bytes.
        /// </summary>
        /// <param name="rva">The relative virtual address.</param>
        int GetOffset(int rva);

        /// <summary>
        /// Try to get the ReadyToRun header RVA for this image.
        /// </summary>
        /// <param name="rva">RVA of the ReadyToRun header if available, 0 when not.</param>
        /// <param name="isComposite">true when the reader represents a composite ReadyToRun image, false for regular R2R.</param>
        /// <returns>true when the reader represents a ReadyToRun image (composite or regular), false otherwise.</returns>
        bool TryGetReadyToRunHeader(out int rva, out bool isComposite);

        /// <summary>
        /// Creates standalone assembly metadata from the image's embedded metadata, or returns null if none is present.
        /// </summary>
        /// <remarks>
        /// The IPlatformBinaryReader object MUST not be disposed while the returned MetadataReader is still in use.
        /// The implementer of IPlatformBinaryReader MUST ensure that the returned MetadataReader remains valid until the implementer is disposed.
        /// Failure to do so may result in unsafe behavior.
        /// </remarks>
        MetadataReader GetStandaloneAssemblyMetadata();

        /// <summary>
        /// Creates manifest assembly metadata from the R2R manifest section at the given file offset and size.
        /// </summary>
        /// <remarks>
        /// The IPlatformBinaryReader object MUST not be disposed while the returned MetadataReader is still in use.
        /// The implementer of IPlatformBinaryReader MUST ensure that the returned MetadataReader remains valid until the implementer is disposed.
        /// Failure to do so may result in unsafe behavior.
        /// </remarks>
        /// <param name="offset">File offset of the manifest metadata section.</param>
        /// <param name="size">Size of the manifest metadata section in bytes.</param>
        MetadataReader GetManifestAssemblyMetadata(int offset, int size);
    }
}
