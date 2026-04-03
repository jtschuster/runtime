// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the RuntimeFunctions section.
    /// A sorted array of runtime function entries, one per compiled code region.
    /// </summary>
    public sealed class RuntimeFunctionsTable
    {
        public IReadOnlyList<RuntimeFunctionEntry> Entries { get; }

        private RuntimeFunctionsTable(List<RuntimeFunctionEntry> entries)
        {
            Entries = entries;
        }

        public static RuntimeFunctionsTable Parse(RawReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            int entrySize = reader.CalculateRuntimeFunctionSize();
            int count = section.Size / entrySize;
            bool isAmd64 = reader.Machine == Machine.Amd64;
            var entries = new List<RuntimeFunctionEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int startRva = reader.ImageReader.ReadInt32(ref offset);
                int? endRva = null;
                if (isAmd64)
                    endRva = reader.ImageReader.ReadInt32(ref offset);
                int unwindRva = reader.ImageReader.ReadInt32(ref offset);
                entries.Add(new RuntimeFunctionEntry(i, startRva, endRva, unwindRva));
            }

            return new RuntimeFunctionsTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the RuntimeFunctions table.
    /// </summary>
    public sealed class RuntimeFunctionEntry
    {
        /// <summary>Index of this entry in the RuntimeFunctions array.</summary>
        public int Index { get; }

        /// <summary>RVA of the start of the code.</summary>
        public int StartRva { get; }

        /// <summary>RVA of the end of the code (Amd64 only; null on other architectures).</summary>
        public int? EndRva { get; }

        /// <summary>RVA of the unwind information.</summary>
        public int UnwindRva { get; }

        public RuntimeFunctionEntry(int index, int startRva, int? endRva, int unwindRva)
        {
            Index = index;
            StartRva = startRva;
            EndRva = endRva;
            UnwindRva = unwindRva;
        }
    }
}
