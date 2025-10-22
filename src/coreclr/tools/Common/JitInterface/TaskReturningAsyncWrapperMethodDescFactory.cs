// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Internal.TypeSystem;

namespace Internal.JitInterface
{
    internal class TaskReturningAsyncWrapperMethodDescFactory : Dictionary<MethodDesc, TaskReturningAsyncWrapperMethodDesc>
    {
        public TaskReturningAsyncWrapperMethodDesc GetTaskReturningAsyncWrapperMethod(MethodDesc method)
        {
            if (!TryGetValue(method, out TaskReturningAsyncWrapperMethodDesc result))
            {
                result = new TaskReturningAsyncWrapperMethodDesc(method, this);
                Add(method, result);
            }

            return result;
        }
    }
}
