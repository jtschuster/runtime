// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Debug = System.Diagnostics.Debug;

namespace Internal.IL.Stubs
{
   public class AsyncResumptionStub : ILStubMethod
    {
        private MethodDesc _method;
        private MethodSignature _signature;
        private MethodIL _methodIL;
        public AsyncResumptionStub(MethodDesc method)
        {
            _method = method;
            _signature = null;
            _methodIL = null;
        }
        public override string DiagnosticName => $"IL_STUB_AsyncResume__{_method.DiagnosticName}";

        public override TypeDesc OwningType => _method.OwningType;

        public override MethodSignature Signature => _signature ??= BuildResumptionStubCalliSignature(_method.Signature);

        private MethodSignature BuildResumptionStubCalliSignature(MethodSignature _)
        {
            var flags = MethodSignatureFlags.None;
            TypeDesc continuation = Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "Continuation"u8);
            ByRefType outputObject = Context.GetByRefType(
                Context.GetWellKnownType(WellKnownType.Byte));
            return new MethodSignature(flags, 0, continuation, [continuation, outputObject]);
        }

        public override TypeSystemContext Context => _method.Context;

        private static int _classCode = new Random().Next(int.MinValue, int.MaxValue);
        protected override int ClassCode => _classCode;

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
            IL.Stubs.ILLocalVariable resultLocal = default;
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

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer) => throw new NotImplementedException();
    }
}
