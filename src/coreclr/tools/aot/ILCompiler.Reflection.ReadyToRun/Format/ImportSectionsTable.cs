// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;

using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Format
{
    /// <summary>
    /// Structural projection of the ImportSections section.
    /// Each entry is a raw import section descriptor without decoded signatures.
    /// </summary>
    public sealed class ImportSectionsTable
    {
        public IReadOnlyList<ImportSectionEntry> Entries { get; }

        private ImportSectionsTable(List<ImportSectionEntry> entries)
        {
            Entries = entries;
        }

        public static ImportSectionsTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            int endOffset = offset + section.Size;
            var entries = new List<ImportSectionEntry>();
            int index = 0;

            while (offset < endOffset)
            {
                int sectionRva = reader.ImageReader.ReadInt32(ref offset);
                int sectionSize = reader.ImageReader.ReadInt32(ref offset);
                var flags = (ReadyToRunImportSectionFlags)reader.ImageReader.ReadUInt16(ref offset);
                var type = (ReadyToRunImportSectionType)reader.ImageReader.ReadByte(ref offset);
                byte entrySize = reader.ImageReader.ReadByte(ref offset);

                if (entrySize == 0)
                {
                    entrySize = reader.Machine switch
                    {
                        Machine.I386 or Machine.ArmThumb2 => 4,
                        Machine.Amd64 or Machine.Arm64 or Machine.LoongArch64 or Machine.RiscV64 => 8,
                        _ => throw new System.NotImplementedException(reader.Machine.ToString()),
                    };
                }

                int signatureRva = reader.ImageReader.ReadInt32(ref offset);
                int auxiliaryDataRva = reader.ImageReader.ReadInt32(ref offset);

                int entryCount = entrySize != 0 ? sectionSize / entrySize : 0;

                entries.Add(new ImportSectionEntry(
                    index++,
                    sectionRva,
                    sectionSize,
                    flags,
                    type,
                    entrySize,
                    entryCount,
                    signatureRva,
                    auxiliaryDataRva));
            }

            return new ImportSectionsTable(entries);
        }
    }

    /// <summary>
    /// A single raw import section descriptor.
    /// </summary>
    public sealed class ImportSectionEntry
    {
        /// <summary>Index of this import section.</summary>
        public int Index { get; }

        /// <summary>RVA of the section containing values to be fixed up.</summary>
        public int SectionRva { get; }

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

        /// <summary>RVA of optional signature descriptors.</summary>
        public int SignatureRva { get; }

        /// <summary>RVA of optional auxiliary data (typically GC info).</summary>
        public int AuxiliaryDataRva { get; }

        public ImportSectionEntry(
            int index,
            int sectionRva,
            int sectionSize,
            ReadyToRunImportSectionFlags flags,
            ReadyToRunImportSectionType type,
            byte entrySize,
            int entryCount,
            int signatureRva,
            int auxiliaryDataRva)
        {
            Index = index;
            SectionRva = sectionRva;
            SectionSize = sectionSize;
            Flags = flags;
            Type = type;
            EntrySize = entrySize;
            EntryCount = entryCount;
            SignatureRva = signatureRva;
            AuxiliaryDataRva = auxiliaryDataRva;
        }
    }
}
