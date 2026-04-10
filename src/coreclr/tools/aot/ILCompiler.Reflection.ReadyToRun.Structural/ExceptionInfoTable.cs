// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// Structural projection of the ExceptionInfo section.
    /// Each entry maps a method RVA to its EH info RVA.
    /// </summary>
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
        public ExceptionInfoTable GetExceptionInfoTable(ReadyToRunSectionHandle section)
        {
            int offset = GetOffsetForRVA(section.RelativeVirtualAddress);
            int length = section.Size;
            var entries = new List<ExceptionInfoEntry>();

            while (length >= 2 * sizeof(int))
            {
                var methodRva = (CodeRva)_nativeReader.ReadInt32(ref offset);
                var ehInfoRva = (EHInfoHandle)_nativeReader.ReadInt32(ref offset);
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
