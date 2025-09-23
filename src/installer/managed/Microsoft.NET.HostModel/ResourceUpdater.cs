// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Microsoft.NET.HostModel.Win32Resources;

namespace Microsoft.NET.HostModel
{
    /// <summary>
    /// Provides methods for modifying the embedded native resources in a PE image.
    /// </summary>
    public class ResourceUpdater : IDisposable
    {
        private readonly FileStream stream;
        private readonly PEReader _reader;
        private ResourceData _resourceData;
        private readonly bool leaveOpen;

        /// <summary>
        /// Create a resource updater for the given PE file.
        /// Resources can be added to this updater, which will queue them for update.
        /// The target PE file will not be modified until Update() is called, after
        /// which the ResourceUpdater can not be used for further updates.
        /// </summary>
        public ResourceUpdater(string peFile)
            : this(new FileStream(peFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        /// <summary>
        /// Create a resource updater for the given PE file. This
        /// Resources can be added to this updater, which will queue them for update.
        /// The target PE file will not be modified until Update() is called, after
        /// which the ResourceUpdater can not be used for further updates.
        /// </summary>
        public ResourceUpdater(FileStream stream, bool leaveOpen = false)
        {
            this.stream = stream;
            this.leaveOpen = leaveOpen;
            try
            {
                this.stream.Seek(0, SeekOrigin.Begin);
                _reader = new PEReader(this.stream, PEStreamOptions.LeaveOpen);
                _resourceData = new ResourceData(_reader);
            }
            catch (Exception)
            {
                if (!leaveOpen)
                    this.stream?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Add all resources from a source PE file. It is assumed
        /// that the input is a valid PE file. If it is not, an
        /// exception will be thrown. This will not modify the target
        /// until Update() is called.
        /// Throws an InvalidOperationException if Update() was already called.
        /// </summary>
        public ResourceUpdater AddResourcesFromPEImage(string peFile)
        {
            if (_resourceData == null)
                ThrowExceptionForInvalidUpdate();

            using var module = new PEReader(File.OpenRead(peFile));
            var moduleResources = new ResourceData(module);
            _resourceData.CopyResourcesFrom(moduleResources);
            return this;
        }

        internal static bool IsIntResource(IntPtr lpType)
        {
            return ((uint)lpType >> 16) == 0;
        }

        private const int LangID_LangNeutral_SublangNeutral = 0;

        /// <summary>
        /// Add a language-neutral integer resource from a byte[] with
        /// a particular type and name. This will not modify the
        /// target until Update() is called.
        /// Throws an InvalidOperationException if Update() was already called.
        /// </summary>
        public ResourceUpdater AddResource(byte[] data, IntPtr lpType, IntPtr lpName)
        {
            if (!IsIntResource(lpType) || !IsIntResource(lpName))
            {
                throw new ArgumentException("AddResource can only be used with integer resource types");
            }
            if (_resourceData == null)
                ThrowExceptionForInvalidUpdate();

            _resourceData.AddResource((ushort)lpName, (ushort)lpType, LangID_LangNeutral_SublangNeutral, data);

            return this;
        }

        /// <summary>
        /// Add a language-neutral integer resource from a byte[] with
        /// a particular type and name. This will not modify the
        /// target until Update() is called.
        /// Throws an InvalidOperationException if Update() was already called.
        /// </summary>
        public ResourceUpdater AddResource(byte[] data, string lpType, IntPtr lpName)
        {
            if (!IsIntResource(lpName))
            {
                throw new ArgumentException("AddResource can only be used with integer resource names");
            }
            if (_resourceData == null)
                ThrowExceptionForInvalidUpdate();

            _resourceData.AddResource((ushort)lpName, lpType, LangID_LangNeutral_SublangNeutral, data);

            return this;
        }

        /// <summary>
        /// Write the pending resource updates to the target PE
        /// file. After this, the ResourceUpdater no longer maintains
        /// an update handle, and can not be used for further updates.
        /// Throws an InvalidOperationException if Update() was already called.
        /// </summary>
        public void Update()
        {
            if (_resourceData == null)
                ThrowExceptionForInvalidUpdate();

            int resourceSectionIndex = _reader.PEHeaders.SectionHeaders.Length;
            for (int i = 0; i < _reader.PEHeaders.SectionHeaders.Length; i++)
            {
                if (_reader.PEHeaders.SectionHeaders[i].Name == ".rsrc")
                {
                    resourceSectionIndex = i;
                    break;
                }
            }

            int fileAlignment = _reader.PEHeaders.PEHeader!.FileAlignment;
            int sectionAlignment = _reader.PEHeaders.PEHeader!.SectionAlignment;

            bool needsAddSection = resourceSectionIndex == _reader.PEHeaders.SectionHeaders.Length;
            bool isRsrcIsLastSection;
            int rsrcPointerToRawData;
            int rsrcVirtualAddress;
            int rsrcOriginalRawDataSize;
            int rsrcOriginalVirtualSize;
            if (needsAddSection)
            {
                isRsrcIsLastSection = true;
                SectionHeader lastSection = _reader.PEHeaders.SectionHeaders.Last();
                rsrcPointerToRawData = GetAligned(lastSection.PointerToRawData + lastSection.SizeOfRawData, fileAlignment);
                rsrcVirtualAddress = GetAligned(lastSection.VirtualAddress + lastSection.VirtualSize, sectionAlignment);
                rsrcOriginalRawDataSize = 0;
                rsrcOriginalVirtualSize = 0;
            }
            else
            {
                isRsrcIsLastSection = _reader.PEHeaders.SectionHeaders.Length - 1 == resourceSectionIndex;
                SectionHeader resourceSection = _reader.PEHeaders.SectionHeaders[resourceSectionIndex];
                rsrcPointerToRawData = resourceSection.PointerToRawData;
                rsrcVirtualAddress = resourceSection.VirtualAddress;
                rsrcOriginalRawDataSize = resourceSection.SizeOfRawData;
                rsrcOriginalVirtualSize = resourceSection.VirtualSize;
            }

            var objectDataBuilder = new ObjectDataBuilder();
            _resourceData.WriteResources(rsrcVirtualAddress, ref objectDataBuilder);
            var rsrcSectionData = objectDataBuilder.ToData();

            int rsrcSectionDataSize = rsrcSectionData.Length;
            int newSectionSize = GetAligned(rsrcSectionDataSize, fileAlignment);
            int newSectionVirtualSize = GetAligned(rsrcSectionDataSize, sectionAlignment);

            int delta = newSectionSize - GetAligned(rsrcOriginalRawDataSize, fileAlignment);
            int virtualDelta = newSectionVirtualSize - GetAligned(rsrcOriginalVirtualSize, sectionAlignment);

            int trailingSectionVirtualStart = rsrcVirtualAddress + rsrcOriginalVirtualSize;
            int trailingSectionStart = rsrcPointerToRawData + rsrcOriginalRawDataSize;
            int trailingSectionLength = (int)(stream.Length - trailingSectionStart);

            bool needsMoveTrailingSections = !isRsrcIsLastSection && delta > 0;
            long finalImageSize = trailingSectionStart + Math.Max(delta, 0) + trailingSectionLength;

            if (finalImageSize > stream.Length)
            {
                stream.SetLength(finalImageSize);
            }

            // Helpers for random access modifications
            static int ReadI32(FileStream s, long pos)
            {
                byte[] buf = new byte[4];
                s.Seek(pos, SeekOrigin.Begin);
                int read = s.Read(buf, 0, 4);
                if (read != 4) throw new IOException("Unexpected EOF reading Int32");
                return buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
            }
            static void WriteI32(FileStream s, long pos, int value)
            {
                byte[] buf = new byte[4];
                buf[0] = (byte)(value & 0xFF);
                buf[1] = (byte)((value >> 8) & 0xFF);
                buf[2] = (byte)((value >> 16) & 0xFF);
                buf[3] = (byte)((value >> 24) & 0xFF);
                s.Seek(pos, SeekOrigin.Begin);
                s.Write(buf, 0, 4);
            }
            static void WriteI16(FileStream s, long pos, short value)
            {
                byte[] buf = new byte[2];
                buf[0] = (byte)(value & 0xFF);
                buf[1] = (byte)((value >> 8) & 0xFF);
                s.Seek(pos, SeekOrigin.Begin);
                s.Write(buf, 0, 2);
            }
            static void ModifyI32(FileStream s, long pos, Func<int, int> modifier)
            {
                int current = ReadI32(s, pos);
                WriteI32(s, pos, modifier(current));
            }

            int peSignatureOffset = ReadI32(stream, PEOffsets.DosStub.PESignatureOffset);
            int sectionBase = peSignatureOffset + PEOffsets.PEHeaderSize + (ushort)_reader.PEHeaders.CoffHeader.SizeOfOptionalHeader;

            if (needsAddSection)
            {
                int resourceSectionBase = sectionBase + PEOffsets.OneSectionHeaderSize * resourceSectionIndex;
                if (resourceSectionBase + PEOffsets.OneSectionHeaderSize > _reader.PEHeaders.SectionHeaders[0].PointerToRawData)
                    throw new InvalidOperationException("Cannot add section header");

                WriteI32(stream, peSignatureOffset + PEOffsets.PEHeader.NumberOfSections, resourceSectionIndex + 1);

                // Write section name ".rsrc\0\0\0"
                byte[] name = new byte[8] { 0x2E, 0x72, 0x73, 0x72, 0x63, 0x00, 0x00, 0x00 };
                stream.Seek(resourceSectionBase, SeekOrigin.Begin);
                stream.Write(name, 0, name.Length);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.VirtualSize, rsrcSectionDataSize);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.VirtualAddress, rsrcVirtualAddress);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.RawSize, newSectionSize);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.RawPointer, rsrcPointerToRawData);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.RelocationsPointer, 0);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.LineNumbersPointer, 0);
                WriteI16(stream, resourceSectionBase + PEOffsets.SectionHeader.NumberOfRelocations, 0);
                WriteI16(stream, resourceSectionBase + PEOffsets.SectionHeader.NumberOfLineNumbers, 0);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.SectionCharacteristics, (int)(SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead));
            }

            if (needsMoveTrailingSections)
            {
                // Move trailing bytes forward by delta, from end to start to avoid overlap
                const int BufferSize = 64 * 1024;
                byte[] buffer = new byte[BufferSize];
                long bytesToMove = trailingSectionLength;
                while (bytesToMove > 0)
                {
                    int chunk = (int)Math.Min(BufferSize, bytesToMove);
                    long srcPos = trailingSectionStart + bytesToMove - chunk;
                    stream.Seek(srcPos, SeekOrigin.Begin);
                    int read = stream.Read(buffer, 0, chunk);
                    if (read != chunk) throw new IOException("Unexpected EOF while moving sections");
                    stream.Seek(srcPos + delta, SeekOrigin.Begin);
                    stream.Write(buffer, 0, chunk);
                    bytesToMove -= chunk;
                }

                // Adjust subsequent section headers
                for (int i = resourceSectionIndex + 1; i < _reader.PEHeaders.SectionHeaders.Length; i++)
                {
                    int currentSectionBase = sectionBase + PEOffsets.OneSectionHeaderSize * i;
                    ModifyI32(stream, currentSectionBase + PEOffsets.SectionHeader.VirtualAddress, v => v + virtualDelta);
                    ModifyI32(stream, currentSectionBase + PEOffsets.SectionHeader.RawPointer, v => v + delta);
                }
            }

            if (rsrcSectionDataSize != rsrcOriginalVirtualSize)
            {
                int resourceSectionBase = sectionBase + PEOffsets.OneSectionHeaderSize * resourceSectionIndex;
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.VirtualSize, rsrcSectionDataSize);
                WriteI32(stream, resourceSectionBase + PEOffsets.SectionHeader.RawSize, newSectionSize);

                void PatchRVA(int offset)
                {
                    ModifyI32(stream, offset, ptr => ptr >= trailingSectionVirtualStart ? ptr + virtualDelta : ptr);
                }

                int dataDirectoriesOffset = _reader.PEHeaders.PEHeader.Magic == PEMagic.PE32Plus
                    ? peSignatureOffset + PEOffsets.PEHeader.PE64DataDirectories
                    : peSignatureOffset + PEOffsets.PEHeader.PE32DataDirectories;

                ModifyI32(stream, peSignatureOffset + PEOffsets.PEHeader.InitializedDataSize, sz => sz + delta);
                ModifyI32(stream, peSignatureOffset + PEOffsets.PEHeader.SizeOfImage, sz => sz + virtualDelta);

                if (needsMoveTrailingSections)
                {
                    for (int i = 0; i < _reader.PEHeaders.PEHeader.NumberOfRvaAndSizes; i++)
                        PatchRVA(dataDirectoriesOffset + i * PEOffsets.DataDirectoryEntrySize + PEOffsets.DataDirectoryEntry.VirtualAddressOffset);
                }

                int resourceTableOffset = dataDirectoriesOffset + PEOffsets.ResourceTableDataDirectoryIndex * PEOffsets.DataDirectoryEntrySize;
                WriteI32(stream, resourceTableOffset + PEOffsets.DataDirectoryEntry.VirtualAddressOffset, rsrcVirtualAddress);
                WriteI32(stream, resourceTableOffset + PEOffsets.DataDirectoryEntry.SizeOffset, rsrcSectionDataSize);
            }

            // Write new resource section data
            stream.Seek(rsrcPointerToRawData, SeekOrigin.Begin);
            stream.Write(rsrcSectionData, 0, rsrcSectionDataSize);

            // Zero the padding
            int padding = newSectionSize - rsrcSectionDataSize;
            if (padding > 0)
            {
                byte[] zero = new byte[Math.Min(padding, 8192)];
                while (padding > 0)
                {
                    int chunk = Math.Min(zero.Length, padding);
                    stream.Write(zero, 0, chunk);
                    padding -= chunk;
                }
            }

            stream.Flush();
            _resourceData = null;
        }

        // Deprecated MemoryMappedFile helper overloads removed after refactor to direct FileStream operations.

        public static int GetAligned(int integer, int alignWith) => (integer + alignWith - 1) & ~(alignWith - 1);

        private static void ThrowExceptionForInvalidUpdate()
        {
            throw new InvalidOperationException(
                "Update handle is invalid. This instance may not be used for further updates");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                _reader.Dispose();
                stream.Dispose();
            }
        }
    }
}
