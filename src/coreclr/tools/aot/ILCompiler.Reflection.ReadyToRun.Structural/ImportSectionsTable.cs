// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;

using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the ImportsTable section.
    /// Each entry is a raw import section descriptor without decoded signatures.
    /// </summary>
    public sealed class ImportSectionsTableSection
    {
        public IReadOnlyList<ImportSectionEntry> Entries { get; }

        internal ImportSectionsTableSection(List<ImportSectionEntry> entries)
        {
            Entries = entries;
        }
    }

    /// <summary>
    /// A single raw import section descriptor.
    /// </summary>
    public sealed class ImportSectionEntry
    {
        /// <summary>RVA of the section containing values to be fixed up.</summary>
        public ImportSlotTableHandle SectionRva { get; }

        /// <summary>Size of the section in bytes.</summary>
        public int SectionSize { get; }

        /// <summary>Import section flags.</summary>
        public ReadyToRunImportSectionFlags Flags { get; }

        /// <summary>Import section type.</summary>
        public ReadyToRunImportSectionType Type { get; }

        /// <summary>Size of each entry in bytes.</summary>
        public byte EntrySize { get; }

        /// <summary>Number of entries in the import section.</summary>
        public int EntryCount { get; }

        /// <summary>RVA of the signature indirection table for this section.</summary>
        public SignatureTableHandle SignatureTableRva { get; }

        /// <summary>RVA of optional auxiliary data (typically GC info).</summary>
        public AuxiliaryDataTableHandle AuxiliaryDataRva { get; }

        internal ImportSectionEntry(
            ImportSlotTableHandle sectionRva,
            int sectionSize,
            ReadyToRunImportSectionFlags flags,
            ReadyToRunImportSectionType type,
            byte entrySize,
            int entryCount,
            SignatureTableHandle signatureTableRva,
            AuxiliaryDataTableHandle auxiliaryDataRva)
        {
            SectionRva = sectionRva;
            SectionSize = sectionSize;
            Flags = flags;
            Type = type;
            EntrySize = entrySize;
            EntryCount = entryCount;
            SignatureTableRva = signatureTableRva;
            AuxiliaryDataRva = auxiliaryDataRva;
        }
    }

    public partial class ReadyToRunReader
    {
        public ImportSectionsTableSection GetImportSectionsTableSection(ReadyToRunSection section)
        {
            int offset = this.GetOffsetForRVA(section.RelativeVirtualAddress);
            int endOffset = offset + section.Size;
            var entries = new List<ImportSectionEntry>();

            while (offset < endOffset)
            {
                int sectionRva = this.ImageReader.ReadInt32(ref offset);
                int sectionSize = this.ImageReader.ReadInt32(ref offset);
                var flags = (ReadyToRunImportSectionFlags)this.ImageReader.ReadUInt16(ref offset);
                var type = (ReadyToRunImportSectionType)this.ImageReader.ReadByte(ref offset);
                byte entrySize = this.ImageReader.ReadByte(ref offset);

                if (entrySize == 0)
                {
                    entrySize = this.Machine switch
                    {
                        Machine.I386 or Machine.ArmThumb2 => 4,
                        Machine.Amd64 or Machine.Arm64 or Machine.LoongArch64 or Machine.RiscV64 => 8,
                        _ => throw new System.NotImplementedException(this.Machine.ToString()),
                    };
                }

                int signatureRva = this.ImageReader.ReadInt32(ref offset);
                int auxiliaryDataRva = this.ImageReader.ReadInt32(ref offset);

                int entryCount = entrySize != 0 ? sectionSize / entrySize : 0;

                entries.Add(new ImportSectionEntry(
                    (ImportSlotTableHandle)sectionRva,
                    sectionSize,
                    flags,
                    type,
                    entrySize,
                    entryCount,
                    (SignatureTableHandle)signatureRva,
                    (AuxiliaryDataTableHandle)auxiliaryDataRva));
            }

            return new ImportSectionsTableSection(entries);
        }
    }

    public enum SignatureHandle {}
    public enum SignatureTableHandle {}
    public enum ImportSlotTableHandle {}
    public enum AuxiliaryDataTableHandle {}
}
