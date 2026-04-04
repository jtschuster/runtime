// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// A reference to a single cell (slot) in a ReadyToRun import section that must be
    /// resolved (fixed up) before the owning method can execute.
    /// </summary>
    public readonly struct FixupCellRef
    {
        /// <summary>
        /// Zero-based index of the import section in the image's
        /// <see cref="ReadyToRunRawReader.ImportSections"/> list.
        /// </summary>
        public uint ImportSectionIndex { get; }

        /// <summary>
        /// Zero-based index of the slot within that import section.
        /// </summary>
        public uint SlotIndex { get; }

        internal FixupCellRef(uint importSectionIndex, uint slotIndex)
        {
            ImportSectionIndex = importSectionIndex;
            SlotIndex = slotIndex;
        }
    }

    /// <summary>
    /// An entry in the MethodDefEntryPoints section, representing the compiled entrypoint
    /// for a single MethodDef in the assembly.
    /// </summary>
    public sealed class MethodDefEntryPoint
    {
        /// <summary>
        /// The 1-based MethodDef row ID (RID) from the assembly metadata.
        /// </summary>
        public uint MethodDefRowId { get; }

        /// <summary>
        /// Index into the RuntimeFunctions table of the entrypoint for this method.
        /// </summary>
        public int RuntimeFunctionIndex { get; }

        /// <summary>
        /// The fixup cells that must be resolved before this method can execute,
        /// or an empty list if there are no pending fixups.
        /// </summary>
        public IReadOnlyList<FixupCellRef> FixupCells { get; }

        internal MethodDefEntryPoint(uint methodDefRowId, int runtimeFunctionIndex, IReadOnlyList<FixupCellRef> fixupCells)
        {
            MethodDefRowId = methodDefRowId;
            RuntimeFunctionIndex = runtimeFunctionIndex;
            FixupCells = fixupCells;
        }
    }

    /// <summary>
    /// Raw projection of a single <c>READYTORUN_IMPORT_SECTION</c> header entry as it appears
    /// in the binary image.  No entries or signatures are pre-decoded.
    /// </summary>
    public sealed class RawImportSection
    {
        /// <summary>Zero-based index of this section in the image's import-section table.</summary>
        public int Index { get; }

        /// <summary>RVA of the section's import cells.</summary>
        public int SectionRva { get; }

        /// <summary>Byte size of the section's import cells.</summary>
        public int SectionSize { get; }

        /// <summary>Flags describing the section's contents.</summary>
        public ReadyToRunImportSectionFlags Flags { get; }

        /// <summary>Type of imports stored in this section.</summary>
        public ReadyToRunImportSectionType Type { get; }

        /// <summary>Size in bytes of each import cell entry.</summary>
        public byte EntrySize { get; }

        /// <summary>RVA of the parallel signature table for this section (0 if absent).</summary>
        public int SignatureRva { get; }

        /// <summary>RVA of auxiliary data for this section (0 if absent).</summary>
        public int AuxiliaryDataRva { get; }

        internal RawImportSection(
            int index,
            int sectionRva,
            int sectionSize,
            ReadyToRunImportSectionFlags flags,
            ReadyToRunImportSectionType type,
            byte entrySize,
            int signatureRva,
            int auxiliaryDataRva)
        {
            Index = index;
            SectionRva = sectionRva;
            SectionSize = sectionSize;
            Flags = flags;
            Type = type;
            EntrySize = entrySize;
            SignatureRva = signatureRva;
            AuxiliaryDataRva = auxiliaryDataRva;
        }
    }

    /// <summary>
    /// Provides direct, low-level projections of the sections present in a ReadyToRun image.
    /// Unlike <see cref="ReadyToRunReader"/>, this class does not resolve metadata tokens or
    /// method handles — every property reflects the raw binary layout of the corresponding
    /// R2R section as described in <c>readytorun-format.md</c>.
    /// </summary>
    public sealed class ReadyToRunRawReader
    {
        private readonly IBinaryImageReader _compositeReader;
        private readonly NativeReader _imageReader;
        private readonly byte[] _image;

        // Parsed lazily
        private ReadyToRunHeader _header;
        private string _compilerIdentifier;
        private List<RawImportSection> _importSections;
        private List<MethodDefEntryPoint> _methodDefEntryPoints;
        private string _ownerCompositeExecutable;
        private bool _ownerCompositeExecutableParsed;

        // ------------------------------------------------------------------ //
        //  Construction
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Opens a ReadyToRun image from a file on disk.
        /// </summary>
        public ReadyToRunRawReader(string filename)
        {
            Filename = filename;
            _image = File.ReadAllBytes(filename);
            _imageReader = new NativeReader(new MemoryStream(_image));

            byte[] img = _image;
            if (MachO.MachObjectFile.IsMachOImage(filename))
            {
                _compositeReader = new MachO.MachOImageReader(img);
            }
            else
            {
                _compositeReader = new PEImageReader(
                    new System.Reflection.PortableExecutable.PEReader(
                        Unsafe.As<byte[], ImmutableArray<byte>>(ref img)));
            }
        }

        /// <summary>
        /// Opens a ReadyToRun image from an already-created PE reader.
        /// </summary>
        public ReadyToRunRawReader(System.Reflection.PortableExecutable.PEReader peReader, string filename)
        {
            Filename = filename;
            _compositeReader = new PEImageReader(peReader);

            ImmutableArray<byte> content = _compositeReader.GetEntireImage();
            _image = Unsafe.As<ImmutableArray<byte>, byte[]>(ref content);
            _imageReader = new NativeReader(new MemoryStream(_image));
        }

        // ------------------------------------------------------------------ //
        //  General image properties
        // ------------------------------------------------------------------ //

        /// <summary>The file name of the image.</summary>
        public string Filename { get; }

        /// <summary>
        /// The top-level ReadyToRun header, which lists all sections present in the image.
        /// </summary>
        public ReadyToRunHeader Header
        {
            get
            {
                EnsureHeader();
                return _header;
            }
        }

        // ------------------------------------------------------------------ //
        //  Section projections
        // ------------------------------------------------------------------ //

        /// <summary>
        /// The compiler identifier string from <c>READYTORUN_SECTION_COMPILER_IDENTIFIER</c>,
        /// or <see langword="null"/> when the section is absent.
        /// </summary>
        public string CompilerIdentifier
        {
            get
            {
                EnsureHeader();
                if (_compilerIdentifier == null
                    && Header.Sections.TryGetValue(ReadyToRunSectionType.CompilerIdentifier, out ReadyToRunSection section))
                {
                    // The section ends with a NUL terminator — exclude it.
                    int offset = GetOffset(section.RelativeVirtualAddress);
                    _compilerIdentifier = Encoding.UTF8.GetString(_image, offset, section.Size - 1);
                }
                return _compilerIdentifier;
            }
        }

        /// <summary>
        /// Raw import section headers from <c>READYTORUN_SECTION_IMPORT_SECTIONS</c>.
        /// Each entry exposes the fields of the binary <c>READYTORUN_IMPORT_SECTION</c>
        /// structure without pre-decoding entries or signatures.
        /// Returns an empty list when the section is absent.
        /// </summary>
        public IReadOnlyList<RawImportSection> ImportSections
        {
            get
            {
                EnsureHeader();
                if (_importSections == null)
                    _importSections = ParseImportSections();
                return _importSections;
            }
        }

        /// <summary>
        /// Parsed entries from the per-assembly <c>READYTORUN_SECTION_METHODDEF_ENTRYPOINTS</c>
        /// NativeArray.  Each entry maps a MethodDef RID to the index of the compiled method in
        /// the RuntimeFunctions table together with the list of import-section slots that must be
        /// fixed up before the method can execute.
        /// Returns an empty list when the section is absent.
        /// </summary>
        /// <remarks>
        /// Only the top-level <c>MethodDefEntryPoints</c> section is parsed. In composite R2R
        /// images, each component assembly has its own section in the per-assembly header; those
        /// per-assembly sections are not included in this list.
        /// </remarks>
        public IReadOnlyList<MethodDefEntryPoint> MethodDefEntryPoints
        {
            get
            {
                EnsureHeader();
                if (_methodDefEntryPoints == null)
                    _methodDefEntryPoints = ParseMethodDefEntryPoints();
                return _methodDefEntryPoints;
            }
        }

        /// <summary>
        /// For per-assembly (non-composite) images that are embedded inside a composite R2R
        /// executable, this is the file name of the owning composite image
        /// (<c>READYTORUN_SECTION_OWNER_COMPOSITE_EXECUTABLE</c>).
        /// Returns <see langword="null"/> when the section is absent.
        /// </summary>
        public string OwnerCompositeExecutable
        {
            get
            {
                EnsureHeader();
                if (!_ownerCompositeExecutableParsed)
                {
                    _ownerCompositeExecutableParsed = true;
                    foreach (ReadyToRunSection section in Header.Sections.Values)
                    {
                        if (section.Type == ReadyToRunSectionType.OwnerCompositeExecutable)
                        {
                            int offset = GetOffset(section.RelativeVirtualAddress);
                            // The section stores a UTF-8 string with a NUL terminator.
                            _ownerCompositeExecutable = Encoding.UTF8.GetString(_image, offset, section.Size - 1);
                            break;
                        }
                    }
                }
                return _ownerCompositeExecutable;
            }
        }

        // ------------------------------------------------------------------ //
        //  Helpers
        // ------------------------------------------------------------------ //

        private void EnsureHeader()
        {
            if (_header != null)
                return;

            if (!_compositeReader.TryGetReadyToRunHeader(out int headerRva, out _))
                throw new BadImageFormatException("The file is not a ReadyToRun image");

            int offset = GetOffset(headerRva);
            _header = new ReadyToRunHeader(_imageReader, headerRva, offset);
        }

        private int GetOffset(int rva) => _compositeReader.GetOffset(rva);

        private int GetPointerSize() =>
            _compositeReader.Machine switch
            {
                System.Reflection.PortableExecutable.Machine.I386
                    or System.Reflection.PortableExecutable.Machine.Arm
                    or System.Reflection.PortableExecutable.Machine.Thumb
                    or System.Reflection.PortableExecutable.Machine.ArmThumb2 => 4,
                _ => 8,
            };

        // ------------------------------------------------------------------ //
        //  Parsing: ImportSections
        // ------------------------------------------------------------------ //

        private List<RawImportSection> ParseImportSections()
        {
            var sections = new List<RawImportSection>();

            if (!Header.Sections.TryGetValue(ReadyToRunSectionType.ImportSections, out ReadyToRunSection importSectionsSection))
                return sections;

            int offset = GetOffset(importSectionsSection.RelativeVirtualAddress);
            int endOffset = offset + importSectionsSection.Size;

            int sectionIndex = 0;
            while (offset < endOffset)
            {
                int sectionRva = _imageReader.ReadInt32(ref offset);
                int sectionSize = _imageReader.ReadInt32(ref offset);
                ushort flags = _imageReader.ReadUInt16(ref offset);
                byte type = _imageReader.ReadByte(ref offset);
                byte entrySize = _imageReader.ReadByte(ref offset);
                if (entrySize == 0)
                    entrySize = (byte)GetPointerSize();
                int signatureRva = _imageReader.ReadInt32(ref offset);
                int auxDataRva = _imageReader.ReadInt32(ref offset);

                sections.Add(new RawImportSection(
                    sectionIndex++,
                    sectionRva,
                    sectionSize,
                    (ReadyToRunImportSectionFlags)flags,
                    (ReadyToRunImportSectionType)type,
                    entrySize,
                    signatureRva,
                    auxDataRva));
            }

            return sections;
        }

        // ------------------------------------------------------------------ //
        //  Parsing: MethodDefEntryPoints
        // ------------------------------------------------------------------ //

        private List<MethodDefEntryPoint> ParseMethodDefEntryPoints()
        {
            var entries = new List<MethodDefEntryPoint>();

            // Single-file images keep MethodDefEntryPoints in the top-level header.
            // Composite images keep per-assembly sections in each assembly header.
            if (Header.Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out ReadyToRunSection section))
            {
                ParseMethodDefEntryPointsSection(section, entries);
            }

            return entries;
        }

        private void ParseMethodDefEntryPointsSection(ReadyToRunSection section, List<MethodDefEntryPoint> entries)
        {
            int methodDefEntryPointsOffset = GetOffset(section.RelativeVirtualAddress);
            NativeArray methodEntryPoints = new NativeArray(_imageReader, (uint)methodDefEntryPointsOffset);
            uint nMethodEntryPoints = methodEntryPoints.GetCount();

            for (uint rid = 1; rid <= nMethodEntryPoints; rid++)
            {
                int entryOffset = 0;
                if (!methodEntryPoints.TryGetAt(rid - 1, ref entryOffset))
                    continue;

                ParseEntryPointEntry(entryOffset, out int runtimeFunctionIndex, out IReadOnlyList<FixupCellRef> fixupCells);
                entries.Add(new MethodDefEntryPoint(rid, runtimeFunctionIndex, fixupCells));
            }
        }

        /// <summary>
        /// Decode a single MethodDef entry point value.
        /// The encoding mirrors <see cref="ReadyToRunReader"/>'s <c>GetRuntimeFunctionIndexFromOffset</c>.
        /// </summary>
        private void ParseEntryPointEntry(int offset, out int runtimeFunctionIndex, out IReadOnlyList<FixupCellRef> fixupCells)
        {
            // The entry is a variable-length unsigned integer:
            //   - low bit == 0  → upper bits are the RuntimeFunction index (>> 1)
            //   - low bit == 1  → bit 1 signals an indirect offset to the fixup list;
            //                     upper bits (>> 2) are the RuntimeFunction index.
            uint id = 0;
            offset = (int)_imageReader.DecodeUnsigned((uint)offset, ref id);
            if ((id & 1) != 0)
            {
                if ((id & 2) != 0)
                {
                    uint val = 0;
                    _imageReader.DecodeUnsigned((uint)offset, ref val);
                    offset -= (int)val;
                }

                runtimeFunctionIndex = (int)(id >> 2);
                fixupCells = ParseFixupList(offset);
            }
            else
            {
                runtimeFunctionIndex = (int)(id >> 1);
                fixupCells = Array.Empty<FixupCellRef>();
            }
        }

        /// <summary>
        /// Decode the nibble-encoded fixup list that follows an entry point.
        /// See the MethodDefEntryPoints section description in readytorun-format.md.
        /// </summary>
        private List<FixupCellRef> ParseFixupList(int fixupOffset)
        {
            var cells = new List<FixupCellRef>();
            NibbleReader nibbles = new NibbleReader(_imageReader, fixupOffset);

            uint importSectionIndex = nibbles.ReadUInt();
            if (importSectionIndex == 0)
                return cells;

            // The first import-section index is 1-based (absolute); convert to 0-based.
            importSectionIndex--;

            while (true)
            {
                // Read the first absolute slot index for this import section.
                uint slotIndex = nibbles.ReadUInt();

                while (true)
                {
                    cells.Add(new FixupCellRef(importSectionIndex, slotIndex));

                    uint slotDelta = nibbles.ReadUInt();
                    if (slotDelta == 0)
                        break;

                    slotIndex += slotDelta;
                }

                uint sectionDelta = nibbles.ReadUInt();
                if (sectionDelta == 0)
                    break;

                importSectionIndex += sectionDelta;
            }

            return cells;
        }
    }
}
