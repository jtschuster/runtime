// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    public partial class AsyncResumptionStub : ILStubMethod
    {
        private readonly MethodDesc _method;
        private MethodSignature _signature;
        private MethodIL _methodIL = null;

        public AsyncResumptionStub(MethodDesc owningMethod)
        {
            Debug.Assert(owningMethod.IsAsyncCall());
            _method = owningMethod;
        }

        public override ReadOnlySpan<byte> Name => _method.Name;
        public override string DiagnosticName => _method.DiagnosticName;

        public override TypeDesc OwningType => _method.OwningType;

        public override MethodSignature Signature => _signature ??= InitializeSignature();

        public override TypeSystemContext Context => _method.Context;

        protected override int ClassCode => unchecked((int)0xa91ac565);

        private MethodSignature InitializeSignature()
        {
            TypeDesc objectType = Context.GetWellKnownType(WellKnownType.Object);
            TypeDesc byrefByte = Context.GetWellKnownType(WellKnownType.Byte).MakeByRefType();
            return _signature = new MethodSignature(0, 0, objectType, [objectType, byrefByte]);
        }

        public override MethodIL EmitIL()
        {
            if (_methodIL != null)
                return _methodIL;
            ILEmitter ilEmitter = new ILEmitter();
            ILCodeStream ilStream = ilEmitter.NewCodeStream();

            // if it has this pointer
            if (!_method.Signature.IsStatic)
            {
                if (_method.OwningType.IsValueType)
                {
                    ilStream.EmitLdc(0);
                    ilStream.Emit(ILOpcode.conv_u);
                }
                else
                {
                    ilStream.Emit(ILOpcode.ldnull);
                }
            }

            foreach (var param in _method.Signature)
            {
                var local = ilEmitter.NewLocal(param);
                ilStream.EmitLdLoca(local);
                ilStream.Emit(ILOpcode.initobj, ilEmitter.NewToken(param));
                ilStream.EmitLdLoc(local);
            }
            ilStream.Emit(ILOpcode.ldftn, ilEmitter.NewToken(_method));
            ilStream.Emit(ILOpcode.calli, ilEmitter.NewToken(this.Signature));

            bool returnsVoid = _method.Signature.ReturnType != Context.GetWellKnownType(WellKnownType.Void);
            Internal.IL.Stubs.ILLocalVariable resultLocal = default;
            if (!returnsVoid)
            {
                resultLocal = ilEmitter.NewLocal(_method.Signature.ReturnType);
                ilStream.EmitStLoc(resultLocal);
            }

            MethodDesc asyncCallContinuation = Context.SystemModule.GetKnownType("System.StubHelpers"u8, "StubHelpers"u8)
                .GetKnownMethod("AsyncCallContinuation"u8, null);
            TypeDesc continuation = Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "Continuation"u8);
            var newContinuationLocal = ilEmitter.NewLocal(continuation);
            ilStream.Emit(ILOpcode.call, ilEmitter.NewToken(asyncCallContinuation));
            ilStream.EmitStLoc(newContinuationLocal);

            if (!returnsVoid)
            {
                var doneResult = ilEmitter.NewCodeLabel();
                ilStream.EmitLdLoca(newContinuationLocal);
                ilStream.Emit(ILOpcode.brtrue, doneResult);
                ilStream.EmitLdArg(1);
                ilStream.EmitLdLoc(resultLocal);
                ilStream.Emit(ILOpcode.stobj, ilEmitter.NewToken(_method.Signature.ReturnType));
                ilStream.EmitLabel(doneResult);
            }
            ilStream.EmitLdLoc(newContinuationLocal);
            ilStream.Emit(ILOpcode.ret);

            return ilEmitter.Link(this);
        }

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            var othr = (AsyncResumptionStub)other;
            return comparer.Compare(_method, othr._method);
        }
    }
}
