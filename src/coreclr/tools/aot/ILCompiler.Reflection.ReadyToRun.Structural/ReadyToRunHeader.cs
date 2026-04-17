// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Fields common to the global R2R header and per-assembly headers in composite R2R images.
    /// </summary>
    public class ReadyToRunCoreHeader
    {
        /// <summary>Flags in the header.</summary>
        public uint Flags { get; }

        /// <summary>The ReadyToRun section handles.</summary>
        public IReadOnlyList<ReadyToRunSection> Sections { get; }

        public ReadyToRunCoreHeader(uint flags, IReadOnlyList<ReadyToRunSection> sections)
        {
            Flags = flags;
            Sections = sections;
        }
    }

    /// <summary>
    /// Structure representing the ReadyToRun header in a PE image.
    /// based on <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/readytorun.h">src/inc/readytorun.h</a> READYTORUN_HEADER
    /// </summary>
    public class ReadyToRunHeader
    {
        // READYTORUN_HEADER fields

        /// <summary>
        /// The expected signature of a ReadyToRun header
        /// </summary>
        public const uint READYTORUN_SIGNATURE = 0x00525452; // 'RTR'

        public uint Signature { get; }

        /// <summary>
        /// The ReadyToRun version
        /// </summary>
        public ushort MajorVersion { get; set; }
        public ushort MinorVersion { get; set; }

        // READYTORUN_CORE_HEADER fields

        /// <summary>
        /// Flags in the header
        /// eg. PLATFORM_NEUTRAL_SOURCE, SKIP_TYPE_VALIDATION
        /// </summary>
        public uint Flags { get; set; }

        /// <summary>
        /// The ReadyToRun section RVAs and sizes
        /// </summary>
        public IReadOnlyList<ReadyToRunSection> Sections { get; private set; }


        public ReadyToRunHeader(uint signature, ushort majorVersion, ushort minorVersion, uint flags, IReadOnlyList<ReadyToRunSection> sections)
        {
            Signature = signature;
            MajorVersion = majorVersion;
            MinorVersion = minorVersion;
            Flags = flags;
            Sections = sections;
        }


        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Signature: 0x{Signature:X8} ('R2R')");
            // sb.AppendLine($"RelativeVirtualAddress: 0x{RelativeVirtualAddress:X8}");
            if (Signature == READYTORUN_SIGNATURE)
            {
                sb.AppendLine($"MajorVersion: 0x{MajorVersion:X4}");
                sb.AppendLine($"MinorVersion: 0x{MinorVersion:X4}");
                sb.AppendLine($"Flags: 0x{Flags:X8}");
                foreach (ReadyToRunFlags flag in Enum.GetValues(typeof(ReadyToRunFlags)))
                {
                    if ((Flags & (uint)flag) != 0)
                    {
                        sb.AppendLine($"  - {Enum.GetName(typeof(ReadyToRunFlags), flag)}");
                    }
                }
            }
            return sb.ToString();
        }
    }

    public partial class ReadyToRunReader
    {
        /// <summary>
        /// Reads a ReadyToRun header from the image at the given file offset.
        /// </summary>
        /// <param name="imageOffset">Index in the image byte array to the start of the ReadyToRun header</param>
        /// <exception cref="BadImageFormatException">The signature must be 0x00525452 ("RTR")</exception>
        public ReadyToRunHeader ReadReadyToRunHeader(int imageOffset)
        {
            var Signature = _nativeReader.ReadUInt32(ref imageOffset);
            if (Signature != ReadyToRunHeader.READYTORUN_SIGNATURE)
            {
                byte[] signature = new byte[sizeof(uint) - 1];
                _nativeReader.ReadSpanAt(ref imageOffset, signature);
                throw new BadImageFormatException("Incorrect R2R header signature: " + Encoding.UTF8.GetString(signature));
            }

            var MajorVersion = _nativeReader.ReadUInt16(ref imageOffset);
            var MinorVersion = _nativeReader.ReadUInt16(ref imageOffset);

            var coreHeader = ReadReadyToRunCoreHeader(ref imageOffset);
            return new ReadyToRunHeader(Signature, MajorVersion, MinorVersion, coreHeader.Flags, coreHeader.Sections);
        }

        /// <summary>
        /// Reads the core header fields (flags + sections) shared by both the global header
        /// and per-assembly headers in composite R2R images.
        /// </summary>
        public ReadyToRunCoreHeader ReadReadyToRunCoreHeader(ref int curOffset)
        {
            uint flags = _nativeReader.ReadUInt32(ref curOffset);
            int nSections = _nativeReader.ReadInt32(ref curOffset);
            var sections = new List<ReadyToRunSection>(nSections);

            for (int i = 0; i < nSections; i++)
            {
                int type = _nativeReader.ReadInt32(ref curOffset);
                var sectionType = (ReadyToRunSectionType)type;
                if (!Enum.IsDefined(typeof(ReadyToRunSectionType), type))
                {
                    throw new BadImageFormatException("Warning: Invalid ReadyToRun section type");
                }
                int sectionStartRva = _nativeReader.ReadInt32(ref curOffset);
                int sectionLength = _nativeReader.ReadInt32(ref curOffset);
                sections.Add(new ReadyToRunSection(sectionType, (ImageRVA)sectionStartRva, sectionLength));
            }
            return new ReadyToRunCoreHeader(flags, sections);
        }
    }
}
