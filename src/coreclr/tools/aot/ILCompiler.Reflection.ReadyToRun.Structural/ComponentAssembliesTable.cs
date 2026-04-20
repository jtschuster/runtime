// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the ComponentAssemblies section.
    /// Each entry contains RVAs to the per-assembly COR header and R2R header.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>AssemblyTableNode</c>.
    /// </remarks>
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
        public ComponentAssembliesTable GetComponentAssembliesTable(ReadyToRunSection section)
        {
            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int count = section.Size / ComponentAssemblyEntry.Size;
            var entries = new List<ComponentAssemblyEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int corHeaderRva = _nativeReader.ReadInt32(ref offset);
                int corHeaderSize = _nativeReader.ReadInt32(ref offset);
                int assemblyHeaderRva = _nativeReader.ReadInt32(ref offset);
                int assemblyHeaderSize = _nativeReader.ReadInt32(ref offset);
                entries.Add(new ComponentAssemblyEntry(corHeaderRva, corHeaderSize, assemblyHeaderRva, assemblyHeaderSize));
            }

            return new ComponentAssembliesTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the ComponentAssemblies table.
    /// </summary>
    public sealed class ComponentAssemblyEntry
    {
        /// <summary>Size in bytes of a single entry on disk (4 × <see cref="int"/>).</summary>
        public const int Size = 4 * sizeof(int);

        /// <summary>RVA of the COR header for this assembly.</summary>
        public int CorHeaderRva { get; }

        /// <summary>Size of the COR header.</summary>
        public int CorHeaderSize { get; }

        /// <summary>
        /// RVA of the per-assembly R2R header (<c>READYTORUN_CORE_HEADER</c>).
        /// </summary>
        /// <remarks>
        /// To enumerate the per-component section table, feed this RVA into
        /// <see cref="ReadyToRunHeader.ReadReadyToRunCoreHeader(ref int)"/> at the
        /// corresponding file offset (use <see cref="ReadyToRunReader.GetOffsetForRVA"/>).
        /// </remarks>
        public int AssemblyHeaderRva { get; }

        /// <summary>Size of the per-assembly R2R header.</summary>
        public int AssemblyHeaderSize { get; }

        public ComponentAssemblyEntry(int corHeaderRva, int corHeaderSize, int assemblyHeaderRva, int assemblyHeaderSize)
        {
            CorHeaderRva = corHeaderRva;
            CorHeaderSize = corHeaderSize;
            AssemblyHeaderRva = assemblyHeaderRva;
            AssemblyHeaderSize = assemblyHeaderSize;
        }
    }
}
