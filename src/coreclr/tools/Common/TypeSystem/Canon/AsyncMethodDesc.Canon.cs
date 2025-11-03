// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Internal.TypeSystem;

namespace Internal.TypeSystem
{
    /// <summary>
    /// Represents the async-callable (CORINFO_CALLCONV_ASYNCCALL) variant of a Task/ValueTask returning method.
    /// </summary>
    public sealed class AsyncMethodDesc : MethodDelegator
    {
        public override MethodDesc GetCanonMethodTarget(CanonicalFormKind kind)
        {
            return _wrappedMethod.GetCanonMethodTarget(kind).GetAsyncOtherVariant();
        }
    }
}
