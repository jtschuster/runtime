// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler
{
    // The compiled *body* of an unboxing stub, emitted into the R2R image. Modeled as an
    // instance method on a BoxedValueType (a reference type with boxed-value-type layout) so the
    // body uses the same calling convention callers use to invoke the unboxing stub: 'this'
    // arrives as a boxed object reference. The body skips the MethodTable* (ldflda RawData.Data)
    // to obtain a byref to the value and tail-calls the unboxed target. The BoxedValueType
    // boxed-`this` modeling is ported from the NativeAOT compiler's BoxedTypes infrastructure.
    // (NativeAOT emits the equivalent non-generic unboxing stub as a hand-written asm thunk,
    // UnboxingStubNode; R2R models it as an IL stub because crossgen2 produces R2R image bodies by
    // JIT-compiling IL.)
    //
    // NOT to be confused with Internal.JitInterface.UnboxingMethodDesc: that is a transient
    // JIT-EE *call-site marker* (a MethodDelegator over the real method, shared with NativeAOT,
    // explicitly never stored) produced by getUnboxingThunk while compiling a *caller* of an
    // unboxing entrypoint. This type is the opposite end: the stored, compilable stub body that
    // such a call-site fixup ultimately binds to at runtime via the instance entry-point table.
    //
    // Restricted to non-generic value types for now.
    internal sealed class UnboxingStubMethod : ILStubMethod, IPrefixMangledMethod
    {
        private readonly MethodDesc _targetMethod;
        private readonly BoxedValueType _boxedType;

        internal UnboxingStubMethod(BoxedValueType boxedType, MethodDesc targetMethod)
        {
            System.Diagnostics.Debug.Assert(targetMethod.OwningType.IsValueType);
            System.Diagnostics.Debug.Assert(!targetMethod.Signature.IsStatic);

            _boxedType = boxedType;
            _targetMethod = targetMethod;
        }

        public override TypeSystemContext Context => _targetMethod.Context;

        // The unboxing stub is an instance method on the boxed value type (a reference type),
        // so the compiled body receives `this` as a boxed object reference.
        public override TypeDesc OwningType => _boxedType;

        public override MethodSignature Signature => _targetMethod.Signature;

        public MethodDesc TargetMethod => _targetMethod;

        public override Utf8Span Name => _targetMethod.Name.Append("_Unbox"u8);

        public override string DiagnosticName => _targetMethod.DiagnosticName + "_Unbox";

        public override Instantiation Instantiation => _targetMethod.Instantiation;

        // The unboxing stub shares the version-resilient hash of the underlying method so
        // that the runtime finds it in the same InstanceEntryPointTable bucket (it is
        // disambiguated by the READYTORUN_METHOD_SIG_UnboxingStub flag on its signature).
        protected override int ComputeHashCode() => _targetMethod.GetHashCode();

        public override MethodIL EmitIL()
        {
            if (_boxedType.ValueTypeRepresented.IsByRefLike)
            {
                // ByRef-like types cannot be boxed, so this thunk is unreachable. Emit a
                // body that throws to keep the pointer-extraction mechanism uniform.
                return new ILStubMethodIL(this,
                    new byte[] { (byte)ILOpcode.ldnull, (byte)ILOpcode.throw_ },
                    Array.Empty<LocalVariableDefinition>(),
                    Array.Empty<object>());
            }

            ILEmitter emit = new ILEmitter();
            ILCodeStream codeStream = emit.NewCodeStream();

            // Load the boxed 'this' and skip over the MethodTable* to get a byref to the
            // value type's data (the 'this' the unboxed target expects).
            codeStream.EmitLdArg(0);
            codeStream.Emit(ILOpcode.ldflda, emit.NewToken(
                Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "RawData"u8).GetField("Data"u8)));

            // Forward the remaining arguments.
            for (int i = 0; i < _targetMethod.Signature.Length; i++)
            {
                codeStream.EmitLdArg(i + 1);
            }

            // Call the unboxed target (becomes an R2R MethodEntry fixup).
            codeStream.Emit(ILOpcode.call, emit.NewToken(_targetMethod));
            codeStream.Emit(ILOpcode.ret);

            return emit.Link(this);
        }

        // IPrefixMangledMethod: mangled from the underlying method with an "unbox" prefix.
        MethodDesc IPrefixMangledMethod.BaseMethod => _targetMethod;
        ReadOnlySpan<byte> IPrefixMangledMethod.Prefix => "unbox"u8;

        // Deterministic ordering support.
        protected override int ClassCode => 446545583;

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_targetMethod, ((UnboxingStubMethod)other)._targetMethod);
        }
    }
}
