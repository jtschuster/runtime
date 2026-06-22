// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler
{
    // PROTOTYPE (Option C v2): a single storable unboxing-thunk MethodDesc that serves BOTH as the
    // call-site identity (returned by getUnboxingThunk for non-generic value types) AND as the
    // compiled stub body emitted into the R2R image.
    //
    // Modeled as a MethodDelegator so it is *transparently* the underlying value-type method
    // everywhere callers observe it: OwningType -> the real value type, and Signature / IsVirtual /
    // attributes / Instantiation are all delegated. That transparency is what lets the same object
    // sit at a call site without tripping the JIT inliner's method-attribute invariants (the wall an
    // ILStubMethod-based unification hit, because an IL stub reports IsVirtual=false).
    //
    // The boxed-`this` receiver (required by the body, which does `ldflda RawData.Data` to skip the
    // MethodTable*) is injected only while the body itself is being compiled: CorInfoImpl.getMethodClass
    // reports BoxedThisType (IMethodWithBoxedThis) when this == MethodBeingCompiled, and the real value
    // type otherwise. The BoxedValueType modeling is ported from the NativeAOT BoxedTypes infrastructure.
    //
    // Contrast with Internal.JitInterface.UnboxingMethodDesc: that is the transient (never stored)
    // call-site marker still used for *generic* value types, whose unboxing stubs are not yet
    // precompiled and continue to be synthesized at runtime.
    //
    // Restricted to non-generic value types for now.
    internal sealed class UnboxingStubMethod : MethodDelegator, IPrefixMangledMethod, IMethodWithBoxedThis
    {
        private readonly BoxedValueType _boxedType;

        internal UnboxingStubMethod(BoxedValueType boxedType, MethodDesc targetMethod)
            : base(targetMethod)
        {
            System.Diagnostics.Debug.Assert(targetMethod.OwningType.IsValueType);
            System.Diagnostics.Debug.Assert(!targetMethod.Signature.IsStatic);
            System.Diagnostics.Debug.Assert(!targetMethod.HasInstantiation);
            System.Diagnostics.Debug.Assert(!targetMethod.OwningType.HasInstantiation);

            _boxedType = boxedType;
        }

        public MethodDesc TargetMethod => _wrappedMethod;

        // Distinct, storable identity (unlike the transient UnboxingMethodDesc whose sorting/hash
        // throw): a unique name/mangling plus ClassCode/CompareToImpl. It deliberately shares the
        // wrapped method's hash so the runtime finds it in the same InstanceEntryPointTable bucket
        // (disambiguated by the READYTORUN_METHOD_SIG_UnboxingStub flag on its signature).
        public override Utf8Span Name => _wrappedMethod.Name.Append("_Unbox"u8);

        public override string DiagnosticName => _wrappedMethod.DiagnosticName + "_Unbox";

        protected override int ComputeHashCode() => _wrappedMethod.GetHashCode();

        // Non-generic only: canonicalization / definition / instantiation collapse to `this`.
        // (GetCanonMethodTarget is abstract on MethodDelegator and MUST be overridden; returning
        // `this` keeps the body node's identity reference-equal to MethodBeingCompiled.)
        public override MethodDesc GetCanonMethodTarget(CanonicalFormKind kind) => this;
        public override MethodDesc GetMethodDefinition() => this;
        public override MethodDesc GetTypicalMethodDefinition() => this;
        public override MethodDesc InstantiateSignature(Instantiation typeInstantiation, Instantiation methodInstantiation) => this;

        // IMethodWithBoxedThis: the boxed-layout receiver the JIT reports for the body's `this`
        // (only while this stub is itself being compiled; see CorInfoImpl.getMethodClass).
        public TypeDesc BoxedThisType => _boxedType;
        MethodDesc IMethodWithBoxedThis.UnboxedTargetMethod => _wrappedMethod;

        public MethodIL EmitIL()
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
            for (int i = 0; i < _wrappedMethod.Signature.Length; i++)
            {
                codeStream.EmitLdArg(i + 1);
            }

            // Call the unboxed target (becomes an R2R MethodEntry fixup).
            codeStream.Emit(ILOpcode.call, emit.NewToken(_wrappedMethod));
            codeStream.Emit(ILOpcode.ret);

            return emit.Link(this);
        }

        // IPrefixMangledMethod: mangled from the underlying method with an "unbox" prefix.
        MethodDesc IPrefixMangledMethod.BaseMethod => _wrappedMethod;
        ReadOnlySpan<byte> IPrefixMangledMethod.Prefix => "unbox"u8;

        // Deterministic ordering support.
        protected override int ClassCode => 446545583;

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_wrappedMethod, ((UnboxingStubMethod)other)._wrappedMethod);
        }
    }
}
