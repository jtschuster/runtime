// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using Internal.TypeSystem;

namespace Internal.JitInterface
{
    /// <summary>
    /// Represents the async-callable (CORINFO_CALLCONV_ASYNCCALL) variant of a Task/ValueTask returning method.
    /// The wrapper should be short‑lived and only used while interacting with the JIT interface.
    /// </summary>
    internal sealed class TaskReturningAsyncWrapperMethodDesc : MethodDelegator, IJitHashableOnly
    {
        private readonly TaskReturningAsyncWrapperMethodDescFactory _factory;
        private readonly int _jitVisibleHashCode;

        public MethodDesc Target => _wrappedMethod;

        public TaskReturningAsyncWrapperMethodDesc(MethodDesc wrappedMethod, TaskReturningAsyncWrapperMethodDescFactory factory)
            : base(wrappedMethod)
        {
            //Debug.Assert(wrappedMethod.ReturnsTaskLike() && wrappedMethod.IsAsync);
            _factory = factory;
            // Salt with arbitrary constant so hash space differs from underlying method.
            _jitVisibleHashCode = HashCode.Combine(wrappedMethod.GetHashCode(), 0x51C0A54);
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
            if (real != _wrappedMethod)
                return _factory.GetTaskReturningAsyncWrapperMethod(real);
            return this;
        }

        public override MethodDesc InstantiateSignature(Instantiation typeInstantiation, Instantiation methodInstantiation)
        {
            MethodDesc real = _wrappedMethod.InstantiateSignature(typeInstantiation, methodInstantiation);
            if (real != _wrappedMethod)
                return _factory.GetTaskReturningAsyncWrapperMethod(real);
            return this;
        }

#if !SUPPORT_JIT
        // Same pattern as UnboxingMethodDesc: these should not escape JIT hashing scope.
        protected override int ClassCode => 0x234;
        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer) => throw new NotImplementedException();
        protected override int ComputeHashCode() => _jitVisibleHashCode;
        int IJitHashableOnly.GetJitVisibleHashCode() => _jitVisibleHashCode;
#else
        int IJitHashableOnly.GetJitVisibleHashCode() => _jitVisibleHashCode;
#endif
    }
}
