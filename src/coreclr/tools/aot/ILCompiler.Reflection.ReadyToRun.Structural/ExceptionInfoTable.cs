// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Structural projection of the ExceptionInfo section.
    /// Each entry maps a method RVA to its EH info RVA.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>ExceptionInfoLookupTableNode</c>.
    /// </remarks>
    public sealed class ExceptionInfoTable
    {
        public IReadOnlyList<ExceptionInfoEntry> Entries { get; }

        internal ExceptionInfoTable(List<ExceptionInfoEntry> entries)
        {
            Entries = entries;
        }
    }

    public partial class ReadyToRunReader
    {
        public ExceptionInfoTable GetExceptionInfoTable(ReadyToRunSection section)
        {
            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int length = section.Size;
            var entries = new List<ExceptionInfoEntry>();

            // The encoding ends with a sentinel record (MethodRva = ~0u, EhInfoRva = endOfEhInfo)
            // used to compute the size of the previous record's clauses. It is not a real entry.
            int totalRecords = length / (2 * sizeof(int));
            int realEntries = totalRecords > 0 ? totalRecords - 1 : 0;

            for (int i = 0; i < realEntries; i++)
            {
                var methodRva = (CodeRva)_nativeReader.ReadInt32(ref offset);
                var ehInfoRva = (EHInfoHandle)_nativeReader.ReadInt32(ref offset);
                entries.Add(new ExceptionInfoEntry(methodRva, ehInfoRva));
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
        public CodeRva MethodRva { get; }

        /// <summary>RVA of the exception handling info.</summary>
        public EHInfoHandle EhInfoRva { get; }

        public ExceptionInfoEntry(CodeRva methodRva, EHInfoHandle ehInfoRva)
        {
            MethodRva = methodRva;
            EhInfoRva = ehInfoRva;
        }
    }

    /// <summary>Opaque handle representing an RVA pointing to exception handling information.</summary>
    public enum EHInfoHandle {}
}
