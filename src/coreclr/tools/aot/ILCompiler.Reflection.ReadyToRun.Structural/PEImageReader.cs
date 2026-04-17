// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Wrapper around <see cref="PEReader"/> that implements <see cref="IPlatformBinaryReader"/>.
    /// </summary>
    public class PEImageReader : IPlatformBinaryReader
    {
        private readonly PEReader _peReader;
        private readonly bool _leaveOpen;

        public Machine Machine { get; }

        public PEImageReader(PEReader peReader, bool leaveOpen = false)
        {
            _peReader = peReader;
            _leaveOpen = leaveOpen;

            // Extract machine and OS from PE header. The OS is encoded as an XOR mask on the machine type.
            uint rawMachine = (uint)_peReader.PEHeaders.CoffHeader.Machine;
            OperatingSystem detectedOs = OperatingSystem.Unknown;

            foreach (OperatingSystem os in System.Enum.GetValues(typeof(OperatingSystem)))
            {
                Machine candidateMachine = (Machine)(rawMachine ^ (uint)os);
                if (System.Enum.IsDefined(typeof(Machine), candidateMachine))
                {
                    Machine = candidateMachine;
                    detectedOs = os;
                    break;
                }
            }

            if (detectedOs == OperatingSystem.Unknown)
            {
                throw new BadImageFormatException($"Invalid PE Machine type: {rawMachine}");
            }
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                _peReader.Dispose();
            }
        }

        public int GetOffset(int rva) => _peReader.GetOffset(rva);

        public bool TryGetReadyToRunHeader(out int rva, out bool isComposite)
        {
            if ((_peReader.PEHeaders.CorHeader.Flags & CorFlags.ILLibrary) == 0)
            {
                // Composite R2R - check for RTR_HEADER export
                if (_peReader.TryGetCompositeReadyToRunHeader(out rva))
                {
                    isComposite = true;
                    return true;
                }
            }
            else
            {
                var r2rHeaderDirectory = _peReader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory;
                if (r2rHeaderDirectory.Size != 0)
                {
                    rva = r2rHeaderDirectory.RelativeVirtualAddress;
                    isComposite = false;
                    return true;
                }
            }

            rva = 0;
            isComposite = false;
            return false;
        }

        public MetadataReader GetStandaloneAssemblyMetadata()
            => _peReader.HasMetadata ? _peReader.GetMetadataReader() : null;

        public unsafe MetadataReader GetManifestAssemblyMetadata(int offset, int size)
        {
            byte* pImage = _peReader.GetEntireImage().Pointer;
            return new MetadataReader(pImage + offset, size);
        }
    }
}
