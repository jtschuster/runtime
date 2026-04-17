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
    /// A single exception handling clause. Plain data — all fields are eagerly populated by the reader.
    /// </summary>
    public sealed class EHClause
    {
        /// <summary>
        /// Length of the serialized EH clause in the PE image (6 × uint).
        /// </summary>
        internal const int Length = 6 * sizeof(uint);

        /// <summary>Flags describing the exception handler.</summary>
        public CorExceptionFlag Flags { get; }

        /// <summary>Starting offset of the try block.</summary>
        public uint TryOffset { get; }

        /// <summary>End offset of the try block.</summary>
        public uint TryEnd { get; }

        /// <summary>Offset of the exception handler for the try block.</summary>
        public uint HandlerOffset { get; }

        /// <summary>End offset of the exception handler.</summary>
        public uint HandlerEnd { get; }

        /// <summary>
        /// For type-based exception handlers, this is the type token.
        /// For filter-based exception handlers, this is the filter offset.
        /// </summary>
        public uint ClassTokenOrFilterOffset { get; }

        public EHClause(CorExceptionFlag flags, uint tryOffset, uint tryEnd, uint handlerOffset, uint handlerEnd, uint classTokenOrFilterOffset)
        {
            Flags = flags;
            TryOffset = tryOffset;
            TryEnd = tryEnd;
            HandlerOffset = handlerOffset;
            HandlerEnd = handlerEnd;
            ClassTokenOrFilterOffset = classTokenOrFilterOffset;
        }

        /// <summary>
        /// Emit a textual representation of the EH clause to a given text writer.
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
    /// EH info for a single runtime function — a simple list of eagerly-parsed EH clauses.
    /// </summary>
    public sealed class EHInfo
    {
        /// <summary>Starting RVA of the corresponding runtime function.</summary>
        public CodeRva MethodRelativeVirtualAddress { get; }

        /// <summary>The EH clauses for this runtime function.</summary>
        public IReadOnlyList<EHClause> EHClauses { get; }

        public EHInfo(CodeRva methodRva, IReadOnlyList<EHClause> clauses)
        {
            MethodRelativeVirtualAddress = methodRva;
            EHClauses = clauses;
        }

        /// <summary>
        /// Emit the textual representation of the EH info into a given writer.
        /// </summary>
        public void WriteTo(TextWriter writer, bool dumpRva)
        {
            foreach (EHClause ehClause in EHClauses)
            {
                ehClause.WriteTo(writer, (int)MethodRelativeVirtualAddress, dumpRva: dumpRva);
                writer.WriteLine();
            }
        }
    }
}

namespace ILCompiler.Reflection.ReadyToRun
{
    public partial class ReadyToRunReader
    {
        private readonly Dictionary<EHInfoHandle, EHInfo> _ehInfoCache = new();

        /// <summary>
        /// Resolve an <see cref="EHInfoHandle"/> to its parsed exception handling information.
        /// The clause count is derived from the byte distance between consecutive EH info RVAs,
        /// so the caller must supply the byte length of the EH info region for this entry.
        /// </summary>
        /// <param name="handle">Handle pointing to the EH info RVA.</param>
        /// <param name="methodRva">Code RVA of the method this EH info belongs to.</param>
        /// <param name="ehInfoByteLength">Byte length of the EH info region (distance to next entry or section end).</param>
        public EHInfo GetEHInfo(EHInfoHandle handle, CodeRva methodRva, int ehInfoByteLength)
        {
            if (_ehInfoCache.TryGetValue(handle, out EHInfo cached))
                return cached;

            int clauseCount = ehInfoByteLength / EHClause.Length;
            if (clauseCount <= 0)
            {
                _ehInfoCache[handle] = null;
                return null;
            }

            int offset = GetOffsetForRVA((int)handle);
            var clauses = new List<EHClause>(clauseCount);
            for (int i = 0; i < clauseCount; i++)
            {
                var flags = (CorExceptionFlag)ImageReader.ReadUInt32(ref offset);
                uint tryOffset = ImageReader.ReadUInt32(ref offset);
                uint tryEnd = ImageReader.ReadUInt32(ref offset);
                uint handlerOffset = ImageReader.ReadUInt32(ref offset);
                uint handlerEnd = ImageReader.ReadUInt32(ref offset);
                uint classTokenOrFilterOffset = ImageReader.ReadUInt32(ref offset);
                clauses.Add(new EHClause(flags, tryOffset, tryEnd, handlerOffset, handlerEnd, classTokenOrFilterOffset));
            }

            var result = new EHInfo(methodRva, clauses);
            _ehInfoCache[handle] = result;

            return result;
        }
    }
}
