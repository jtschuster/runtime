// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ILCompiler.Reflection.ReadyToRun.Structural;

namespace ILCompiler.Reflection.ReadyToRun.MachO
{
    /// <summary>
    /// Wrapper around Mach-O file that implements IBinaryImageReader
    /// </summary>
    public class MachOImageReader : IBinaryImageReader, IDisposable
    {
        private readonly byte[] _image;
        private readonly MachHeader _header;
        private int? _rtrHeaderRva;
        private readonly GCHandle _pinnedArray;

        public Machine Machine { get; }
        public Structural.OperatingSystem OperatingSystem => Structural.OperatingSystem.Apple;
        public ulong ImageBase => 0;

        public MachOImageReader(byte[] image)
        {
            _image = image;
            _pinnedArray = GCHandle.Alloc(_image, GCHandleType.Pinned);

            // Read the MachO header
            Read(0, out _header);
            if (!_header.Is64Bit)
                throw new BadImageFormatException("Only 64-bit Mach-O files are supported");

            // Determine machine type from CPU type
            Machine = GetMachineType(_header.CpuType);

            // Mach-O MH_OBJECT files have unresolved relocations. crossgen2 emits
            // IMAGE_REL_BASED_ADDR32NB and IMAGE_REL_SYMBOL_SIZE as SUBTRACTOR+UNSIGNED
            // relocation pairs. Apply them so RVA fields in the R2R header are resolved.
            ApplyRelocations();
        }

        ~MachOImageReader()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool _)
        {
            if (_pinnedArray.IsAllocated)
            {
                _pinnedArray.Free();
            }
        }

        public ImmutableArray<byte> GetEntireImage()
            => Unsafe.As<byte[], ImmutableArray<byte>>(ref Unsafe.AsRef(in _image));

        public int GetOffset(int rva)
        {
            // Use section-level file offset, not segment-level. Sections within a segment
            // have independent file offsets that may differ from segment.fileoff + (rva - segment.vmaddr)
            // due to Mach-O header packing.
            if (TryGetContainingSection((ulong)rva, out Section64LoadCommand section))
            {
                ulong offsetWithinSection = (ulong)rva - section.GetVMAddress(_header);
                ulong fileOffset = section.GetFileOffset(_header) + offsetWithinSection;
                System.Diagnostics.Debug.Assert(fileOffset <= int.MaxValue);
                return (int)fileOffset;
            }
            else
            {
                throw new BadImageFormatException("Failed to convert RVA to offset: " + rva);
            }
        }

        public bool TryGetReadyToRunHeader(out int rva, out bool isComposite)
        {
            if (!_rtrHeaderRva.HasValue)
            {
                // Look for RTR_HEADER symbol in the Mach-O symbol table
                // Mach-O R2R images are always composite (no regular R2R format)
                if (TryFindSymbol("RTR_HEADER", out ulong symbolValue))
                {
                    System.Diagnostics.Debug.Assert(symbolValue <= int.MaxValue);
                    _rtrHeaderRva = (int)symbolValue;
                }
                else
                {
                    _rtrHeaderRva = 0;
                }
            }

            rva = _rtrHeaderRva.Value;
            isComposite = rva != 0; // Mach-O R2R images are always composite
            return rva != 0;
        }

        public MetadataReader GetStandaloneAssemblyMetadata() => null;

        public unsafe MetadataReader GetManifestAssemblyMetadata(int offset, int size)
        {
            return new MetadataReader((byte*)Unsafe.AsPointer(ref _image[offset]), size);
        }

        public void DumpImageInformation(TextWriter writer)
        {
            writer.WriteLine($"FileType: {_header.FileType}");
            writer.WriteLine($"CpuType: 0x{_header.CpuType:X}");
            writer.WriteLine($"NumberOfCommands: {_header.NumberOfCommands}");
            writer.WriteLine($"SizeOfCommands: {_header.SizeOfCommands} byte(s)");

            writer.WriteLine("Sections:");
            EnumerateSections((segmentName, section) =>
            {
                string sectionName = section.SectionName.GetString();
                ulong vmAddr = section.GetVMAddress(_header);
                ulong size = section.GetSize(_header);
                writer.WriteLine($"  {segmentName},{sectionName,-16} 0x{vmAddr:X8} - 0x{vmAddr + size:X8}");
            });
        }

        public Dictionary<string, int> GetSections()
        {
            Dictionary<string, int> sectionMap = [];
            EnumerateSections((segmentName, section) =>
            {
                string sectionName = section.SectionName.GetString();
                ulong size = section.GetSize(_header);
                System.Diagnostics.Debug.Assert(size <= int.MaxValue);
                sectionMap[$"{segmentName},{sectionName}"] = (int)size;
            });
            return sectionMap;
        }

        private unsafe void EnumerateSections(Action<string, Section64LoadCommand> callback)
        {
            long commandsPtr = sizeof(MachHeader);
            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.Segment64)
                {
                    Read(commandsPtr, out Segment64LoadCommand segment);
                    uint sectionsCount = segment.GetSectionsCount(_header);
                    string segmentName = segment.Name.GetString();

                    // Sections come immediately after the segment load command
                    long sectionPtr = commandsPtr + sizeof(Segment64LoadCommand);
                    for (uint j = 0; j < sectionsCount; j++)
                    {
                        Read(sectionPtr, out Section64LoadCommand section);
                        callback(segmentName, section);
                        sectionPtr += sizeof(Section64LoadCommand);
                    }
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }
        }

        private static Machine GetMachineType(uint cpuType)
        {
            // https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/osfmk/mach/machine.h
            const uint CPU_TYPE_ARM64 = 0x0100000C;
            const uint CPU_TYPE_X86_64 = 0x01000007;
            return cpuType switch
            {
                CPU_TYPE_ARM64 => Machine.Arm64,
                CPU_TYPE_X86_64 => Machine.Amd64,
                _ => throw new NotSupportedException($"Unsupported MachO CPU type: {cpuType:X8}")
            };
        }

        /// <summary>
        /// Finds a symbol in the symbol table by name.
        /// </summary>
        /// <param name="symbolName">The name of the symbol to find (without leading underscore).</param>
        /// <param name="symbolValue">The value of the symbol if found.</param>
        /// <returns>True if the symbol was found, false otherwise.</returns>
        private unsafe bool TryFindSymbol(string symbolName, out ulong symbolValue)
        {
            symbolValue = 0;

            // Find the symbol table load command
            long commandsPtr = sizeof(MachHeader);
            SymbolTableLoadCommand symtabCommand = default;
            bool foundSymtab = false;

            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.SymbolTable)
                {
                    Read(commandsPtr, out symtabCommand);
                    foundSymtab = true;
                    break;
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }

            if (!foundSymtab || symtabCommand.IsDefault)
            {
                return false;
            }

            uint symbolTableOffset = symtabCommand.GetSymbolTableOffset(_header);
            uint symbolsCount = symtabCommand.GetSymbolsCount(_header);
            uint stringTableOffset = symtabCommand.GetStringTableOffset(_header);
            uint stringTableSize = symtabCommand.GetStringTableSize(_header);

            for (uint i = 0; i < symbolsCount; i++)
            {
                long symOffset = symbolTableOffset + (i * sizeof(NList64));

                // Read the symbol table entry
                Read(symOffset, out NList64 symbol);

                uint strIndex = symbol.GetStringTableIndex(_header);
                if (strIndex >= stringTableSize)
                {
                    continue;
                }

                // Read symbol name from string table
                string name = ReadCString(stringTableOffset + strIndex, stringTableSize - strIndex);

                // Symbol names in Mach-O can have a leading underscore
                if (name == symbolName || (name.Length > 0 && name[0] == '_' && name.AsSpan(1).SequenceEqual(symbolName)))
                {
                    symbolValue = symbol.GetValue(_header);
                    return true;
                }
            }

            return false;
        }

        // Mach-O relocation type constants
        // See https://github.com/apple-oss-distributions/cctools/blob/main/include/mach-o/x86_64/reloc.h
        // and https://github.com/apple-oss-distributions/cctools/blob/main/include/mach-o/arm64/reloc.h
        private const byte X86_64_RELOC_UNSIGNED = 0;
        private const byte X86_64_RELOC_SUBTRACTOR = 5;
        private const byte ARM64_RELOC_UNSIGNED = 0;
        private const byte ARM64_RELOC_SUBTRACTOR = 1;

        /// <summary>
        /// Applies Mach-O relocations to resolve RVA fields in the image.
        /// crossgen2 emits MH_OBJECT files where IMAGE_REL_BASED_ADDR32NB and IMAGE_REL_SYMBOL_SIZE
        /// are represented as SUBTRACTOR+UNSIGNED relocation pairs. The SUBTRACTOR symbol is typically
        /// __mh_dylib_header (the image base). The resolved value is:
        ///   unsigned_symbol.value - subtractor_symbol.value + existing_addend
        /// </summary>
        private unsafe void ApplyRelocations()
        {
            // Build symbol value table from the symbol table load command
            ulong[] symbolValues = BuildSymbolValueTable();
            if (symbolValues is null)
                return;

            bool isArm64 = Machine == Machine.Arm64;
            byte subtractorType = isArm64 ? ARM64_RELOC_SUBTRACTOR : X86_64_RELOC_SUBTRACTOR;
            byte unsignedType = isArm64 ? ARM64_RELOC_UNSIGNED : X86_64_RELOC_UNSIGNED;

            // Iterate all sections and apply their relocations
            long commandsPtr = sizeof(MachHeader);
            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.Segment64)
                {
                    Read(commandsPtr, out Segment64LoadCommand segment);
                    uint sectionsCount = segment.GetSectionsCount(_header);

                    long sectionPtr = commandsPtr + sizeof(Segment64LoadCommand);
                    for (uint j = 0; j < sectionsCount; j++)
                    {
                        Read(sectionPtr, out Section64LoadCommand section);
                        uint relocOffset = section.GetRelocationOffset(_header);
                        uint relocCount = section.GetNumberOfRelocationEntries(_header);
                        uint sectionFileOffset = section.GetFileOffset(_header);

                        if (relocCount > 0)
                        {
                            ApplySectionRelocations(
                                symbolValues, relocOffset, relocCount,
                                sectionFileOffset, subtractorType, unsignedType);
                        }

                        sectionPtr += sizeof(Section64LoadCommand);
                    }
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }
        }

        /// <summary>
        /// Builds an array mapping symbol index to symbol value from the Mach-O symbol table.
        /// </summary>
        private unsafe ulong[] BuildSymbolValueTable()
        {
            long commandsPtr = sizeof(MachHeader);
            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.SymbolTable)
                {
                    Read(commandsPtr, out SymbolTableLoadCommand symtabCommand);
                    uint symbolTableOffset = symtabCommand.GetSymbolTableOffset(_header);
                    uint symbolsCount = symtabCommand.GetSymbolsCount(_header);

                    ulong[] values = new ulong[symbolsCount];
                    for (uint s = 0; s < symbolsCount; s++)
                    {
                        long symOffset = symbolTableOffset + (s * sizeof(NList64));
                        Read(symOffset, out NList64 symbol);
                        values[s] = symbol.GetValue(_header);
                    }
                    return values;
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }

            return null;
        }

        /// <summary>
        /// Applies SUBTRACTOR+UNSIGNED relocation pairs for a single section.
        /// </summary>
        private void ApplySectionRelocations(
            ulong[] symbolValues,
            uint relocOffset,
            uint relocCount,
            uint sectionFileOffset,
            byte subtractorType,
            byte unsignedType)
        {
            for (uint r = 0; r < relocCount; r++)
            {
                long relocEntryOffset = relocOffset + (r * 8);
                int rAddress = BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan((int)relocEntryOffset, 4));
                uint rInfo = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan((int)relocEntryOffset + 4, 4));

                uint symbolNum = rInfo & 0x00FF_FFFF;
                byte length = (byte)((rInfo >> 25) & 0x3);
                bool isExternal = (rInfo & 0x0800_0000) != 0;
                byte relocType = (byte)((rInfo >> 28) & 0xF);

                if (relocType != subtractorType)
                    continue;

                // SUBTRACTOR must be followed by UNSIGNED at the same address
                if (r + 1 >= relocCount)
                    throw new BadImageFormatException("SUBTRACTOR relocation without following UNSIGNED relocation");

                r++;
                long nextRelocOffset = relocOffset + (r * 8);
                int nextRAddress = BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan((int)nextRelocOffset, 4));
                uint nextRInfo = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan((int)nextRelocOffset + 4, 4));

                uint nextSymbolNum = nextRInfo & 0x00FF_FFFF;
                byte nextLength = (byte)((nextRInfo >> 25) & 0x3);
                bool nextIsExternal = (nextRInfo & 0x0800_0000) != 0;
                byte nextRelocType = (byte)((nextRInfo >> 28) & 0xF);

                if (nextRelocType != unsignedType || nextRAddress != rAddress || nextLength != length)
                {
                    throw new BadImageFormatException(
                        $"Invalid SUBTRACTOR+UNSIGNED relocation pair: " +
                        $"SUBTRACTOR(addr=0x{rAddress:X},len={length},type={relocType}) " +
                        $"UNSIGNED(addr=0x{nextRAddress:X},len={nextLength},type={nextRelocType})");
                }

                if (!isExternal || !nextIsExternal)
                {
                    throw new BadImageFormatException(
                        "Non-external SUBTRACTOR+UNSIGNED relocation pair is not supported");
                }

                if (symbolNum >= (uint)symbolValues.Length || nextSymbolNum >= (uint)symbolValues.Length)
                {
                    throw new BadImageFormatException(
                        $"Relocation symbol index out of range: {symbolNum} or {nextSymbolNum} >= {symbolValues.Length}");
                }

                ulong subtractorValue = symbolValues[symbolNum];
                ulong unsignedValue = symbolValues[nextSymbolNum];

                // r_address is section-relative
                int patchFileOffset = (int)sectionFileOffset + rAddress;

                // length: 2 = 4 bytes (int32), 3 = 8 bytes (int64)
                if (length == 2)
                {
                    int addend = BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan(patchFileOffset, 4));
                    long resolved = (long)unsignedValue - (long)subtractorValue + addend;
                    BinaryPrimitives.WriteInt32LittleEndian(_image.AsSpan(patchFileOffset, 4), checked((int)resolved));
                }
                else if (length == 3)
                {
                    long addend = BinaryPrimitives.ReadInt64LittleEndian(_image.AsSpan(patchFileOffset, 8));
                    long resolved = (long)unsignedValue - (long)subtractorValue + addend;
                    BinaryPrimitives.WriteInt64LittleEndian(_image.AsSpan(patchFileOffset, 8), resolved);
                }
                else
                {
                    throw new BadImageFormatException(
                        $"Unsupported SUBTRACTOR+UNSIGNED relocation length: {length} (expected 2 or 3)");
                }
            }
        }

        /// <summary>
        /// Finds the section that contains the specified virtual memory address.
        /// </summary>
        private unsafe bool TryGetContainingSection(ulong vmAddress, out Section64LoadCommand section)
        {
            section = default;

            long commandsPtr = sizeof(MachHeader);
            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.Segment64)
                {
                    Read(commandsPtr, out Segment64LoadCommand seg);
                    uint sectionsCount = seg.GetSectionsCount(_header);

                    long sectionPtr = commandsPtr + sizeof(Segment64LoadCommand);
                    for (uint j = 0; j < sectionsCount; j++)
                    {
                        Read(sectionPtr, out Section64LoadCommand sec);
                        ulong secAddr = sec.GetVMAddress(_header);
                        ulong secSize = sec.GetSize(_header);
                        if (vmAddress >= secAddr && vmAddress < secAddr + secSize)
                        {
                            section = sec;
                            return true;
                        }
                        sectionPtr += sizeof(Section64LoadCommand);
                    }
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }

            return false;
        }

        /// <summary>
        /// Finds the segment that contains the specified virtual memory address.
        /// </summary>
        /// <param name="vmAddress">The virtual memory address to find.</param>
        /// <param name="segment">The segment containing the VM address if found.</param>
        /// <returns>True if a containing segment was found, false otherwise.</returns>
        private unsafe bool TryGetContainingSegment(ulong vmAddress, out Segment64LoadCommand segment)
        {
            segment = default;

            // Iterate through all load commands to find segments
            long commandsPtr = sizeof(MachHeader);

            for (int i = 0; i < _header.NumberOfCommands; i++)
            {
                Read(commandsPtr, out LoadCommand loadCommand);

                if (loadCommand.GetCommandType(_header) == MachLoadCommandType.Segment64)
                {
                    Read(commandsPtr, out Segment64LoadCommand seg);

                    // Check if the VM address falls within this segment
                    ulong segmentVMAddr = seg.GetVMAddress(_header);
                    ulong segmentVMSize = seg.GetVMSize(_header);
                    if (vmAddress >= segmentVMAddr && vmAddress < segmentVMAddr + segmentVMSize)
                    {
                        segment = seg;
                        return true;
                    }
                }

                commandsPtr += loadCommand.GetCommandSize(_header);
            }

            return false;
        }

        /// <summary>
        /// Reads a null-terminated C string from the image.
        /// </summary>
        private string ReadCString(uint offset, uint maxLength)
        {
            if (offset < 0 || offset >= _image.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            // Find the null terminator in the image array
            int length;
            long end = Math.Min(offset + maxLength, _image.Length);
            for (length = 0; offset + length < end; length++)
            {
                if (_image[offset + length] == 0)
                {
                    break;
                }
            }

            System.Diagnostics.Debug.Assert(offset <= int.MaxValue);
            return System.Text.Encoding.UTF8.GetString(_image, (int)offset, length);
        }

        public void Read<T>(long offset, out T result) where T : unmanaged
        {
            unsafe
            {
                if (offset < 0 || offset + sizeof(T) > _image.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                fixed (byte* ptr = &_image[offset])
                {
                    result = Unsafe.ReadUnaligned<T>(ptr);
                }
            }
        }
    }
}
