// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// If COR_ILMETHOD_SECT_HEADER::Kind() = CorILMethod_Sect_EHTable then the attribute
    /// is a list of exception handling clauses.  There are two formats, fat or small
    /// </summary>
    [Flags]
    public enum CorExceptionFlag
    {
        COR_ILEXCEPTION_CLAUSE_NONE,                    // This is a typed handler
        COR_ILEXCEPTION_CLAUSE_OFFSETLEN = 0x0000,      // Deprecated
        COR_ILEXCEPTION_CLAUSE_DEPRECATED = 0x0000,     // Deprecated
        COR_ILEXCEPTION_CLAUSE_FILTER = 0x0001,         // If this bit is on, then this EH entry is for a filter
        COR_ILEXCEPTION_CLAUSE_FINALLY = 0x0002,        // This clause is a finally clause
        COR_ILEXCEPTION_CLAUSE_FAULT = 0x0004,          // Fault clause (finally that is called on exception only)
        COR_ILEXCEPTION_CLAUSE_DUPLICATED = 0x0008,     // duplicated clause. This clause was duplicated to a funclet which was pulled out of line
        COR_ILEXCEPTION_CLAUSE_SAMETRY = 0x0010,        // This clause covers same try block as the previous one
        COR_ILEXCEPTION_CLAUSE_R2R_SYSTEM_EXCEPTION = 0x0020, // R2R only: This clause catches System.Exception

        COR_ILEXCEPTION_CLAUSE_KIND_MASK = COR_ILEXCEPTION_CLAUSE_FILTER | COR_ILEXCEPTION_CLAUSE_FINALLY | COR_ILEXCEPTION_CLAUSE_FAULT,
    }

    /// <summary>
    /// A single exception handling clause decoded from the image.
    /// This is a raw projection — it exposes the token but not the resolved class name.
    /// </summary>
    public class EHClause
    {
        /// <summary>
        /// Length of the serialized EH clause in the PE image.
        /// </summary>
        internal const int Length = 6 * sizeof(uint);

        /// <summary>
        /// Flags describing the exception handler.
        /// </summary>
        public CorExceptionFlag Flags { get; }

        /// <summary>
        /// Starting offset of the try block
        /// </summary>
        public uint TryOffset { get; }

        /// <summary>
        /// End offset of the try block
        /// </summary>
        public uint TryEnd { get; }

        /// <summary>
        /// Offset of the exception handler for the try block
        /// </summary>
        public uint HandlerOffset { get; }

        /// <summary>
        /// End offset of the exception handler
        /// </summary>
        public uint HandlerEnd { get; }

        /// <summary>
        /// For type-based exception handlers, this is the type token.
        /// For filter-based exception handlers, this is the filter offset.
        /// </summary>
        public uint ClassTokenOrFilterOffset { get; }

        /// <summary>
        /// Read the EH clause from a given file offset in the PE image.
        /// </summary>
        public EHClause(NativeReader imageReader, int offset)
        {
            Flags = (CorExceptionFlag)imageReader.ReadUInt32(ref offset);
            TryOffset = imageReader.ReadUInt32(ref offset);
            TryEnd = imageReader.ReadUInt32(ref offset);
            HandlerOffset = imageReader.ReadUInt32(ref offset);
            HandlerEnd = imageReader.ReadUInt32(ref offset);
            ClassTokenOrFilterOffset = imageReader.ReadUInt32(ref offset);
        }

        /// <summary>
        /// Emit a textual representation of the EH info to a given text writer.
        /// </summary>
        public void WriteTo(TextWriter writer, int methodRva, bool dumpRva)
        {
            writer.Write($"Flags {(uint)Flags:X2} ");
            writer.Write($"TryOff {TryOffset:X4} ");
            if (dumpRva)
                writer.Write($"(RVA {(TryOffset + methodRva):X4}) ");
            writer.Write($"TryEnd {TryEnd:X4} ");
            if (dumpRva)
                writer.Write($"(RVA {(TryEnd + methodRva):X4}) ");
            writer.Write($"HndOff {HandlerOffset:X4} ");
            if (dumpRva)
                writer.Write($"(RVA {(HandlerOffset + methodRva):X4}) ");
            writer.Write($"HndEnd {HandlerEnd:X4} ");
            if (dumpRva)
                writer.Write($"(RVA {(HandlerEnd + methodRva):X4}) ");
            writer.Write($"ClsFlt {ClassTokenOrFilterOffset:X4}");

            switch (Flags & CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_KIND_MASK)
            {
                case CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_NONE:
                    writer.Write(" CATCH");
                    break;

                case CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_FILTER:
                    writer.Write($" FILTER (RVA {(ClassTokenOrFilterOffset + methodRva):X4})");
                    break;

                case CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_FINALLY:
                    writer.Write(" FINALLY");
                    break;

                case CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_FAULT:
                    writer.Write(" FAULT");
                    break;

                default:
                    throw new NotImplementedException(Flags.ToString());
            }

            if ((Flags & CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_DUPLICATED) != (CorExceptionFlag)0)
            {
                writer.Write(" DUPLICATED");
            }

            if ((Flags & CorExceptionFlag.COR_ILEXCEPTION_CLAUSE_SAMETRY) != (CorExceptionFlag)0)
            {
                writer.Write(" SAMETRY");
            }
        }
    }

    /// <summary>
    /// EH info for a single runtime function, decoded from a single offset in the image.
    /// </summary>
    public class EHInfo
    {
        private readonly NativeReader _imageReader;
        private readonly int _offset;
        private readonly int _clauseCount;

        /// <summary>
        /// RVA of the EH info in the PE image.
        /// </summary>
        public int RelativeVirtualAddress { get; }

        /// <summary>
        /// Starting RVA of the corresponding runtime function.
        /// </summary>
        public int MethodRelativeVirtualAddress { get; }

        private List<EHClause> _clauses;

        /// <summary>
        /// List of EH clauses for the runtime function.
        /// </summary>
        public IReadOnlyList<EHClause> EHClauses
        {
            get
            {
                _clauses ??= ParseClauses();
                return _clauses;
            }
        }

        public EHInfo(NativeReader imageReader, int ehInfoRva, int methodRva, int offset, int clauseCount)
        {
            _imageReader = imageReader;
            RelativeVirtualAddress = ehInfoRva;
            MethodRelativeVirtualAddress = methodRva;
            _offset = offset;
            _clauseCount = clauseCount;
        }

        private List<EHClause> ParseClauses()
        {
            var clauses = new List<EHClause>(_clauseCount);
            for (int i = 0; i < _clauseCount; i++)
            {
                clauses.Add(new EHClause(_imageReader, _offset + i * EHClause.Length));
            }

            return clauses;
        }

        /// <summary>
        /// Emit the textual representation of the EH info into a given writer.
        /// </summary>
        public void WriteTo(TextWriter writer, bool dumpRva)
        {
            foreach (EHClause ehClause in EHClauses)
            {
                ehClause.WriteTo(writer, MethodRelativeVirtualAddress, dumpRva: dumpRva);
                writer.WriteLine();
            }
        }
    }
}
