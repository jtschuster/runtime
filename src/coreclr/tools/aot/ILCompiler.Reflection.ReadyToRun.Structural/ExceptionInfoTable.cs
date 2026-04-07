// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the ExceptionInfo section.
    /// Each entry maps a method RVA to its EH info RVA.
    /// </summary>
    public sealed class ExceptionInfoTable
    {
        public IReadOnlyList<ExceptionInfoEntry> Entries { get; }

        private ExceptionInfoTable(List<ExceptionInfoEntry> entries)
        {
            Entries = entries;
        }

        public static ExceptionInfoTable Parse(ReadyToRunReader reader, ReadyToRunSection section)
        {
            int offset = reader.GetOffset(section.RelativeVirtualAddress);
            int length = section.Size;
            var entries = new List<ExceptionInfoEntry>();

            // Each record is 2 ints: (methodRva, ehInfoRva).
            // We read pairs until we exhaust the section.
            while (length >= 2 * sizeof(int))
            {
                int methodRva = reader.ImageReader.ReadInt32(ref offset);
                int ehInfoRva = reader.ImageReader.ReadInt32(ref offset);
                entries.Add(new ExceptionInfoEntry(methodRva, ehInfoRva));
                length -= 2 * sizeof(int);
            }

            return new ExceptionInfoTable(entries);
        }
    }

    /// <summary>
    /// A single entry in the ExceptionInfo table: maps a method code RVA
    /// to the RVA of its exception handling information.
    /// </summary>
    public sealed class ExceptionInfoEntry
    {
        /// <summary>RVA of the method code.</summary>
        public int MethodRva { get; }

        /// <summary>RVA of the exception handling info.</summary>
        public int EhInfoRva { get; }

        public ExceptionInfoEntry(int methodRva, int ehInfoRva)
        {
            MethodRva = methodRva;
            EhInfoRva = ehInfoRva;
        }
    }
}
