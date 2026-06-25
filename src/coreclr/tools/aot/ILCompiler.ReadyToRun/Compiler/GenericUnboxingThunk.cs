// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler
{
    // R2R port of NativeAOT's GenericUnboxingThunk
    // (ILCompiler.Compiler/Compiler/CompilerTypeSystemContext.BoxedTypes.cs).
    //
    // This is the *shared-generic* counterpart to the non-generic UnboxingStubMethod. Unlike that
    // type (a MethodDelegator over a non-generic value-type method), this thunk is a proper typical
    // method definition owned by a generic boxed-layout reference type (BoxedValueType, e.g.
    // Boxed_Foo<T>). It is instantiated to the canonical boxed type (Boxed_Foo<__Canon>) via
    // GetMethodForInstantiatedType, so the JIT sees `this` as a boxed object reference with the
    // correct calling convention -- no getMethodClass/IMethodWithBoxedThis hook is required.
    //
    // Because the owning type is generic, the body loads the boxed object's MethodTable and feeds it
    // to SetNextCallGenericContext (the hidden generic context the canonical instance method needs),
    // then dispatches to the *open* target (Foo<!0>.Bar). The ReadyToRunILProvider wraps the
    // instantiated thunk body in InstantiatedMethodIL, which substitutes !0 -> __Canon before R2R's
    // loadable-type validation runs.
    internal sealed class GenericUnboxingThunk : ILStubMethod, IPrefixMangledMethod
    {
        private readonly MethodDesc _targetMethod;
        private readonly BoxedValueType _owningType;

        internal GenericUnboxingThunk(BoxedValueType owningType, MethodDesc targetMethod)
        {
            System.Diagnostics.Debug.Assert(targetMethod.OwningType.IsValueType);
            System.Diagnostics.Debug.Assert(!targetMethod.Signature.IsStatic);

            _owningType = owningType;
            _targetMethod = targetMethod;
        }

        public override TypeSystemContext Context => _targetMethod.Context;

        public override TypeDesc OwningType => _owningType;

        public override MethodSignature Signature => _targetMethod.Signature;

        public MethodDesc TargetMethod => _targetMethod;

        public override Utf8Span Name => _targetMethod.Name.Append("_Unbox"u8);

        public override string DiagnosticName => _targetMethod.DiagnosticName + "_Unbox";

        public override MethodIL EmitIL()
        {
            if (_owningType.ValueTypeRepresented.IsByRefLike)
            {
                // ByRef-like types cannot be boxed, so this thunk is unreachable. Emit a
                // body that throws to keep the pointer-extraction mechanism uniform.
                return new ILStubMethodIL(this,
                    new byte[] { (byte)ILOpcode.ldnull, (byte)ILOpcode.throw_ },
                    Array.Empty<LocalVariableDefinition>(),
                    Array.Empty<object>());
            }

            // Generate the generic unboxing stub. This loosely corresponds to the following C#:
            //   return BoxedValue.InstanceMethod(MethodTableOf(this), [rest of parameters])
            // where the MethodTable* is the hidden generic-context argument extracted from the
            // boxed object header (see the detailed comment below for why it is read directly
            // instead of calling RuntimeHelpers.GetMethodTable).

            ILEmitter emit = new ILEmitter();
            ILCodeStream codeStream = emit.NewCodeStream();

            FieldDesc rawDataField = Context.SystemModule
                .GetKnownType("System.Runtime.CompilerServices"u8, "RawData"u8).GetField("Data"u8);

            // Load the boxed 'this' and skip over the MethodTable* to get a byref to the value
            // type's data (the 'this' the unboxed target expects).
            codeStream.EmitLdArg(0);
            codeStream.Emit(ILOpcode.ldflda, emit.NewToken(rawDataField));

            // Load the MethodTable of the boxed valuetype (the hidden generic context parameter
            // expected by the canonical instance method, but normally not part of the IL signature)
            // and pass it to SetNextCallGenericContext. R2R has no well-known m_pEEType field like
            // NativeAOT, and RuntimeHelpers.GetMethodTable is an intrinsic that cannot be encoded as
            // a version-resilient R2R method token. So the MethodTable* is read directly out of the
            // object header exactly the way the runtime's own unboxing IL stub does
            // (CreateUnboxingILStubForValueTypeMethods in vm/prestub.cpp): take the interior byref to
            // the first field (ldflda RawData.Data), back up by the object header size
            // (Object::GetOffsetOfFirstField() == sizeof(object header) == pointer size) to the
            // object base, then load the MethodTable* stored there.
            codeStream.EmitLdArg(0);
            codeStream.Emit(ILOpcode.ldflda, emit.NewToken(rawDataField));
            codeStream.EmitLdc(Context.Target.PointerSize);
            codeStream.Emit(ILOpcode.sub);
            codeStream.Emit(ILOpcode.ldind_i);
            codeStream.Emit(ILOpcode.call, emit.NewToken(
                Context.GetCoreLibEntryPoint("System.Runtime.CompilerServices"u8, "RuntimeHelpers"u8, "SetNextCallGenericContext"u8, null)));

            // Forward the remaining arguments.
            for (int i = 0; i < _targetMethod.Signature.Length; i++)
            {
                codeStream.EmitLdArg(i + 1);
            }

            if (_targetMethod.IsAsyncCall())
            {
                codeStream.Emit(ILOpcode.call, emit.NewToken(
                    Context.GetCoreLibEntryPoint("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8, "TailAwait"u8, null)));
            }

            // Call the open target (Foo<!0>.Bar). InstantiatedMethodIL substitutes !0 -> __Canon
            // when the instantiated thunk's IL is materialized; the call becomes an R2R MethodEntry
            // fixup.
            codeStream.Emit(ILOpcode.call, emit.NewToken(_targetMethod.InstantiateAsOpen()));
            codeStream.Emit(ILOpcode.ret);

            return emit.Link(this);
        }

        // IPrefixMangledMethod: mangled from the underlying method with an "unbox" prefix.
        MethodDesc IPrefixMangledMethod.BaseMethod => _targetMethod;
        ReadOnlySpan<byte> IPrefixMangledMethod.Prefix => "unbox"u8;

        // Deterministic ordering support (matches NativeAOT's GenericUnboxingThunk ClassCode).
        protected override int ClassCode => -247515475;

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_targetMethod, ((GenericUnboxingThunk)other)._targetMethod);
        }
    }

    internal static class GenericUnboxingThunkExtensions
    {
        /// <summary>
        /// Does a method represent a (shared-generic) special unboxing thunk?
        /// </summary>
        public static bool IsSpecialUnboxingThunk(this MethodDesc method)
        {
            return method.GetTypicalMethodDefinition() is GenericUnboxingThunk;
        }
    }
}
