// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using Internal.TypeSystem;

namespace Internal.JitInterface
{
    /// <summary>
    /// Represents the async-callable (CORINFO_CALLCONV_ASYNCCALL) variant of a Task/ValueTask returning method.
    /// </summary>
    internal sealed class TaskReturningAsyncWrapperMethodDesc : MethodDelegator
    {
        private readonly TaskReturningAsyncWrapperMethodDescFactory _factory;

        public MethodDesc Target => _wrappedMethod;

        public TaskReturningAsyncWrapperMethodDesc(MethodDesc wrappedMethod, TaskReturningAsyncWrapperMethodDescFactory factory)
            : base(wrappedMethod)
        {
            //Debug.Assert(wrappedMethod.ReturnsTaskLike() && wrappedMethod.IsAsync);
            _factory = factory;
        }

        public override MethodDesc GetCanonMethodTarget(CanonicalFormKind kind)
        {
            MethodDesc realCanonTarget = _wrappedMethod.GetCanonMethodTarget(kind);
            if (realCanonTarget != _wrappedMethod)
                return _factory.GetTaskReturningAsyncWrapperMethod(realCanonTarget);
            return this;
        }

        public override MethodDesc GetMethodDefinition()
        {
            MethodDesc real = _wrappedMethod.GetMethodDefinition();
            if (real != _wrappedMethod)
                return _factory.GetTaskReturningAsyncWrapperMethod(real);
            return this;
        }

        public override MethodDesc GetTypicalMethodDefinition()
        {
            MethodDesc real = _wrappedMethod.GetTypicalMethodDefinition();
            return real;
            //if (real != _wrappedMethod)
            //    return _factory.GetTaskReturningAsyncWrapperMethod(real);
            //return this;
        }

        public override MethodDesc InstantiateSignature(Instantiation typeInstantiation, Instantiation methodInstantiation)
        {
            MethodDesc real = _wrappedMethod.InstantiateSignature(typeInstantiation, methodInstantiation);
            if (real != _wrappedMethod)
                return _factory.GetTaskReturningAsyncWrapperMethod(real);
            return this;
        }

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            if (other is TaskReturningAsyncWrapperMethodDesc otherWrapper)
            {
                return comparer.Compare(_wrappedMethod, otherWrapper._wrappedMethod);
            }
            return -1;
        }

        protected override int ClassCode => 0x7074aea0;
    }
}
