// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection.Metadata;

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    public interface IAssemblyResolver : IDisposable
    {
        /// <summary>
        /// Finds an assembly by its reference handle.
        /// The implementor MUST remain alive and undisposed while the returned MetadataReader is in use.
        /// </summary>
        AssemblyMetadata FindAssembly(MetadataReader metadataReader, AssemblyReferenceHandle assemblyReferenceHandle, string parentFile);

        /// <summary>
        /// Finds an assembly by its simple name.
        /// The implementor MUST remain alive and undisposed while the returned MetadataReader is in use.
        /// </summary>
        AssemblyMetadata FindAssembly(string simpleName, string parentFile);
    }

    public class AssemblyMetadata : IDisposable
    {
        private readonly IDisposable _metadata;
        private readonly MetadataReader _reader;

        public AssemblyMetadata(IDisposable metadata, MetadataReader reader)
        {
            _metadata = metadata;
            _reader = reader;
        }

        public MetadataReader Reader => _reader;

        public void Dispose()
        {
            _metadata?.Dispose();
        }
    }

    public class SignatureFormattingOptions
    {
        public bool Naked { get; set; }
        public bool SignatureBinary { get; set; }
        public bool InlineSignatureBinary { get; set; }
    }
}
