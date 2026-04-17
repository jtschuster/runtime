// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

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
}
