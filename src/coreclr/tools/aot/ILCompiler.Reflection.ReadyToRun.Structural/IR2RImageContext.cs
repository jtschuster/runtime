// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Abstraction over the R2R image that provides the minimum interface needed
    /// by the signature decoder: raw image bytes, reference assembly resolution,
    /// and target pointer size.
    /// </summary>
    /// <remarks>
    /// This replaces the direct dependency on <c>ReadyToRunReader</c> in the
    /// signature decoding infrastructure, enabling the Structural project to
    /// be fully standalone.
    /// </remarks>
    public interface IR2RImageContext
    {
        /// <summary>
        /// Raw bytes of the PE image.
        /// </summary>
        byte[] Image { get; }

        /// <summary>
        /// Size of a pointer on the target architecture (4 or 8).
        /// </summary>
        int TargetPointerSize { get; }

        /// <summary>
        /// Open a reference assembly by its module index in the manifest metadata.
        /// Returns <see cref="IAssemblyMetadata"/> for the referenced module.
        /// </summary>
        IAssemblyMetadata OpenReferenceAssembly(int index);
    }
}
