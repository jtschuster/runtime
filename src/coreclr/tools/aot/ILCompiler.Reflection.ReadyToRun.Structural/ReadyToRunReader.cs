// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Internal.ReadyToRunConstants;
using Internal.Runtime;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// A low-level, structural reader for Ready-to-Run images. Parses headers and sections
    /// without cross-referencing metadata — each section table exposes only the raw indices,
    /// RIDs, offsets, and flags encoded in that section.
    /// Also implements <see cref="IR2RImageContext"/> to support signature decoding.
    /// </summary>
    public sealed partial class ReadyToRunReader : IR2RImageContext, IDisposable
    {
        private readonly IPlatformBinaryReader _platformBinaryReader;
        private readonly NativeReader _nativeReader;
        private readonly string _filename;

        // Lazy-init backing fields
        private ReadyToRunHeader _header;
        private bool? _isComposite;

        public ReadyToRunReader(IPlatformBinaryReader platformBinaryReader, NativeReader nativeReader, string filename = null)
        {
            _platformBinaryReader = platformBinaryReader;
            _nativeReader = nativeReader;
            _filename = filename ?? string.Empty;
        }

        public void Dispose()
        {
            _nativeReader.Dispose();
            _platformBinaryReader.Dispose();
        }

        /// <summary>NativeReader for raw byte access into the image.</summary>
        public NativeReader ImageReader => _nativeReader;

        /// <summary>Filename of the R2R image being read.</summary>
        public string Filename => _filename;

        /// <summary>
        /// Whether component assembly indices in the manifest start at 2 (V6+).
        /// In older formats they start at 1.
        /// </summary>
        public bool ComponentAssemblyIndicesStartAtTwo
        {
            get
            {
                return GetHeader().MajorVersion >= 6;
            }
        }

        /// <summary>Offset used for component assembly index adjustment.</summary>
        public int ComponentAssemblyIndexOffset => ComponentAssemblyIndicesStartAtTwo ? 2 : 1;

        /// <summary>Machine architecture of the image.</summary>
        public Machine Machine
        {
            get
            {
                return _platformBinaryReader.Machine;
            }
        }

        /// <summary>Pointer size for the target architecture (4 or 8).</summary>
        public int TargetPointerSize
        {
            get
            {
                return Machine switch
                {
                    Machine.I386 or Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => 4,
                    Machine.Amd64 or Machine.Arm64 or Machine.LoongArch64 or Machine.RiscV64 => 8,
                    _ => throw new NotImplementedException(Machine.ToString()),
                };
            }
        }

        /// <summary>Whether this is a composite R2R image.</summary>
        public bool Composite
        {
            get
            {
                if (_isComposite.HasValue)
                    return _isComposite.Value;

                if (!_platformBinaryReader.TryGetReadyToRunHeader(out _, out bool isComposite))
                    throw new BadImageFormatException("Image is not a ReadyToRunImage");
                _isComposite = isComposite;

                return _isComposite.Value;
            }
        }

        /// <summary>The parsed R2R header.</summary>
        public ReadyToRunHeader ReadyToRunHeader
        {
            get
            {
                return GetHeader();
            }
        }

        /// <summary>Get the file offset corresponding to an RVA.</summary>
        public int GetOffsetForRVA(int rva) => _platformBinaryReader.GetOffset(rva);

        /// <summary>Get the file offset corresponding to a section RVA.</summary>
        public int GetOffsetForRVA(ImageRVA rva) => _platformBinaryReader.GetOffset((int)rva);

        /// <summary>
        /// All section handles from the global R2R header.
        /// </summary>
        public IReadOnlyList<ReadyToRunSection> GetSections() => GetHeader().Sections;

        public ReadyToRunHeader GetHeader()
        {
            if (_header is not null)
                return _header;

            if (!_platformBinaryReader.TryGetReadyToRunHeader(out int headerRva, out bool isComposite))
                throw new BadImageFormatException("Not a ReadyToRun image");

            _isComposite = isComposite;
            int headerOffset = GetOffsetForRVA(headerRva);
            _header = ReadReadyToRunHeader(headerOffset);
            return _header;
        }

        /// <summary>Gets the compiler identifier string from a CompilerIdentifier section.</summary>
        public string GetCompilerIdentifier(ReadyToRunSection section)
        {
            if (section.Size <= 1)
                return string.Empty;

            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _nativeReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Gets the owner composite executable filename from an OwnerCompositeExecutable section.</summary>
        public string GetOwnerCompositeExecutable(ReadyToRunSection section)
        {
            if (section.Size <= 1)
                return string.Empty;

            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _nativeReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Gets the standalone (component 0) metadata for non-composite images.
        /// Returns null for composite images.
        /// The ReadyToRunReader must remain alive and undisposed while the returned reader is in use.
        /// </summary>
        public MetadataReader GetStandaloneMetadata()
        {
            if (Composite)
                return null;
            return _platformBinaryReader.GetStandaloneAssemblyMetadata();
        }

        /// <summary>
        /// Returns a MetadataReader for the ManifestMetadata module.
        /// The ReadyToRunReader must remain alive and undisposed while the returned reader is in use.
        /// </summary>
        public MetadataReader GetManifestMetadataReader(ReadyToRunSection manifestSection)
        {
            int manifestOffset = GetOffsetForRVA(manifestSection.RelativeVirtualAddress);
            int manifestSize = manifestSection.Size;
            if (manifestSize <= 0)
                return null;

            return _platformBinaryReader.GetManifestAssemblyMetadata(manifestOffset, manifestSize);
        }

        // ── Entry point descriptor decoding ────────────────────────────────

        /// <summary>
        /// Decode the runtime function index and optional fixup offset from a compressed
        /// entry point descriptor at the given image offset. This is the same encoding used
        /// after MethodDef and InstanceMethod signature blobs.
        /// </summary>
        /// <param name="offset">Image offset to start reading from.</param>
        /// <param name="runtimeFunctionIndex">Decoded runtime function index.</param>
        /// <param name="fixupOffset">Fixup list offset, or null if no fixups.</param>
        public void GetRuntimeFunctionIndexFromOffset(int offset, out int runtimeFunctionIndex, out int? fixupOffset)
        {
            fixupOffset = null;

            uint id = 0;
            offset = (int)_nativeReader.DecodeUnsigned((uint)offset, ref id);
            if ((id & 1) != 0)
            {
                if ((id & 2) != 0)
                {
                    uint val = 0;
                    _nativeReader.DecodeUnsigned((uint)offset, ref val);
                    offset -= (int)val;
                }

                fixupOffset = offset;
                id >>= 2;
            }
            else
            {
                id >>= 1;
            }

            runtimeFunctionIndex = (int)id;
        }

        /// <summary>
        /// Decode a fixup signature at the given RVA into an AST representation.
        /// The returned <see cref="R2RFixupSignature"/> contains the fixup kind,
        /// optional module override index, and a payload with raw token RIDs and
        /// module indices — no assembly resolution is performed.
        /// </summary>
        /// <param name="signatureRva">The RVA of the signature in the image.</param>
        /// <returns>The decoded fixup signature AST node.</returns>
        public R2RFixupSignature DecodeFixupSignature(int signatureRva)
        {
            int offset = _platformBinaryReader.GetOffset(signatureRva);
            R2RSignature signature = RawSignatureDecoder.DecodeFixupSignature(_nativeReader, offset, TargetPointerSize);
            return R2RFixupSignature.FromSignature(signature);
        }
    }
}
