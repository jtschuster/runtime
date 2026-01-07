// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Internal.TypeSystem;
using Internal.IL;

using Interlocked = System.Threading.Interlocked;

namespace ILCompiler
{
    public partial class CompilerTypeSystemContext
    {
        public CompilerTypeSystemContext()
        {
            _continuationTypeHashtable = new ContinuationTypeHashtable(this);
        }
    }
}
