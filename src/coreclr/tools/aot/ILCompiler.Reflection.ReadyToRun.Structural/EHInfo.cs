// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun
{
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
    }

    /// <summary>
    /// EH info for a single runtime function — a simple list of eagerly-parsed EH clauses.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: <c>MethodWithGCInfo.EHInfo blob (referenced by ExceptionInfoLookupTableNode)</c>.
    /// </remarks>
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
