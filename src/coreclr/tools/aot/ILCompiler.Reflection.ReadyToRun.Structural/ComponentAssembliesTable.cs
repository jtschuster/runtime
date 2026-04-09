// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the ComponentAssemblies section.
    /// Each entry contains RVAs to the per-assembly COR header and R2R header.
    /// </summary>
    public sealed class ComponentAssembliesTable
    {
        public IReadOnlyList<ComponentAssemblyEntry> Entries { get; }

        internal ComponentAssembliesTable(List<ComponentAssemblyEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public ComponentAssembliesTable GetComponentAssembliesTable(ReadyToRunSectionHandle section)
        {
            int offset = GetOffset(section.RelativeVirtualAddress);
            int count = section.Size / ComponentAssembly.Size;
            var entries = new List<ComponentAssemblyEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int corHeaderRva = _imageReader.ReadInt32(ref offset);
                int corHeaderSize = _imageReader.ReadInt32(ref offset);
                int assemblyHeaderRva = _imageReader.ReadInt32(ref offset);
                int assemblyHeaderSize = _imageReader.ReadInt32(ref offset);
                entries.Add(new ComponentAssemblyEntry(i, corHeaderRva, corHeaderSize, assemblyHeaderRva, assemblyHeaderSize));
            }

            return new ComponentAssembliesTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the ComponentAssemblies table.
    /// </summary>
    public sealed class ComponentAssemblyEntry
    {
        /// <summary>Index of this component assembly.</summary>
        public int Index { get; }

        /// <summary>RVA of the COR header for this assembly.</summary>
        public int CorHeaderRva { get; }

        /// <summary>Size of the COR header.</summary>
        public int CorHeaderSize { get; }

        /// <summary>RVA of the per-assembly R2R header.</summary>
        public int AssemblyHeaderRva { get; }

        /// <summary>Size of the per-assembly R2R header.</summary>
        public int AssemblyHeaderSize { get; }

        public ComponentAssemblyEntry(int index, int corHeaderRva, int corHeaderSize, int assemblyHeaderRva, int assemblyHeaderSize)
        {
            Index = index;
            CorHeaderRva = corHeaderRva;
            CorHeaderSize = corHeaderSize;
            AssemblyHeaderRva = assemblyHeaderRva;
            AssemblyHeaderSize = assemblyHeaderSize;
        }
    }
}
