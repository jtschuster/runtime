// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the RuntimeFunctions section.
    /// A sorted array of runtime function entries, one per compiled code region.
    /// </summary>
    public sealed class RuntimeFunctionsTable
    {
        public IReadOnlyList<RuntimeFunctionEntry> Entries { get; }

        internal RuntimeFunctionsTable(List<RuntimeFunctionEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public RuntimeFunctionsTable GetRuntimeFunctionsTable(ReadyToRunSectionHandle section)
        {
            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int entrySize = CalculateRuntimeFunctionSize();
            int count = section.Size / entrySize;
            bool isAmd64 = Machine == Machine.Amd64;
            var entries = new List<RuntimeFunctionEntry>(count);

            for (int i = 0; i < count; i++)
            {
                var startRva = (CodeRva)_nativeReader.ReadInt32(ref offset);
                CodeRva? endRva = null;
                if (isAmd64)
                    endRva = (CodeRva)_nativeReader.ReadInt32(ref offset);
                var unwindRva = (UnwindInfoHandle)_nativeReader.ReadInt32(ref offset);
                entries.Add(new RuntimeFunctionEntry(i, startRva, endRva, unwindRva));
            }

            return new RuntimeFunctionsTable(entries);
        }

        /// <summary>
        /// Calculates the runtime function entry size based on the target machine.
        /// Amd64 has 3 ints (start, end, unwind); others have 2 (start, unwind).
        /// </summary>
        internal int CalculateRuntimeFunctionSize()
        {
            return Machine == Machine.Amd64 ? 3 * sizeof(int) : 2 * sizeof(int);
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
        public CodeRva StartRva { get; }

        /// <summary>RVA of the end of the code (Amd64 only; null on other architectures).</summary>
        public CodeRva? EndRva { get; }

        /// <summary>RVA of the unwind information.</summary>
        public UnwindInfoHandle UnwindRva { get; }

        public RuntimeFunctionEntry(int index, CodeRva startRva, CodeRva? endRva, UnwindInfoHandle unwindRva)
        {
            Index = index;
            StartRva = startRva;
            EndRva = endRva;
            UnwindRva = unwindRva;
        }
    }

    /// <summary>Opaque handle representing an RVA pointing to code in the image.</summary>
    public enum CodeRva {}

    /// <summary>Opaque handle representing an RVA pointing to unwind information.</summary>
    public enum UnwindInfoHandle {}
}
