// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Enumerates the fixed set of extern functions (mostly NativeAOT runtime helpers) that are referenced by name only (i.e.
    /// without a backing <see cref="MethodDesc"/>) from the JIT interface and dependency analysis, e.g.
    /// from <see cref="JitHelper"/> and CorInfoImpl.RyuJit's GetHelperFtnUncached. Each value corresponds
    /// 1:1 with the mangled native symbol name emitted for it (see <see cref="KnownExternFunctions"/>),
    /// and the mapping must reproduce, byte-for-byte, the strings that were previously constructed ad hoc
    /// at each call site (including their architecture-dependent variants).
    /// </summary>
    public enum KnownExternFunction
    {
        // Exception helpers - Runtime/<arch>/ExceptionHandling.{S,asm}
        ThrowEx,
        Rethrow,
        ThrowExact,
        FallbackFailFast,
        DebugBreak,

        // GC write barriers - Runtime/portable.cpp (RhpAssignRef/RhpCheckedAssignRef) and per-arch asm
        WriteBarrier,
        CheckedWriteBarrier,
        WriteBarrier_EAX,
        WriteBarrier_EBX,
        WriteBarrier_ECX,
        WriteBarrier_EDI,
        WriteBarrier_ESI,
        WriteBarrier_EBP,
        CheckedWriteBarrier_EAX,
        CheckedWriteBarrier_EBX,
        CheckedWriteBarrier_ECX,
        CheckedWriteBarrier_EDI,
        CheckedWriteBarrier_ESI,
        CheckedWriteBarrier_EBP,
        BulkMoveWithWriteBarrier,

        // Allocation helpers - nativeaot/Runtime/portable.cpp, runtime/portable/AllocFast.cpp, and per-arch asm
        NewArray,
        NewObject,
        NewFast,
        NewFinalizable,
        NewFastAlign8,
        NewFinalizableAlign8,
        NewFastMisalign,
        NewPtrArrayFast,
        NewArrayFastAlign8,
        NewArrayFast,

        // libc helpers
        NativeMemSet,
        DblRem,
        FltRem,

        // Runtime/MathHelpers.cpp
        Lng2Dbl,
        ULng2Dbl,
        Lng2Flt,
        ULng2Flt,
        Dbl2Lng,
        Dbl2ULng,
        LMul,
        LRsz,
        LRsh,
        LLsh,

        // Runtime/<arch>/PInvoke.{S,asm}, Runtime/thread.cpp
        PInvokeBegin,
        PInvokeEnd,
        ReversePInvokeEnter,
        ReversePInvokeExit,

        // Interface/generic virtual dispatch stubs - Runtime/EHHelpers.cpp, Runtime/<arch>/DispatchResolve.{S,asm}
        GVMLookupForSlot,
        InterfaceDispatch,
        InterfaceDispatchGuarded,
        ResolveInterfaceMethodFast,
        ResolveInterfaceMethod,

        // Misc
        StackProbe,
        GcPoll,
        NyiLdVirtFtn,
        TlsGetAddr,
    }

    /// <summary>
    /// Names and signatures of the <see cref="KnownExternFunction"/> helpers. The helpers are referenced by name
    /// only, so there is no backing <see cref="MethodDesc"/> to source a signature from; the signature (needed to
    /// import the helper on Wasm) is derived here from each helper's declaration.
    /// </summary>
    internal static class KnownExternFunctions
    {
        public static Utf8String GetName(KnownExternFunction function, TargetDetails target)
        {
            string name = function switch
            {
                KnownExternFunction.ThrowEx => "RhpThrowEx",
                KnownExternFunction.Rethrow => "RhpRethrow",
                KnownExternFunction.ThrowExact => "RhpThrowExact",
                KnownExternFunction.FallbackFailFast => "RhpFallbackFailFast",
                KnownExternFunction.DebugBreak => "RhDebugBreak",

                KnownExternFunction.WriteBarrier => target.Architecture switch
                {
                    TargetArchitecture.ARM64 => "RhpAssignRefArm64",
                    TargetArchitecture.LoongArch64 => "RhpAssignRefLoongArch64",
                    TargetArchitecture.RiscV64 => "RhpAssignRefRiscV64",
                    _ => "RhpAssignRef"
                },
                KnownExternFunction.CheckedWriteBarrier =>
                    target.Architecture == TargetArchitecture.ARM64 ? "RhpCheckedAssignRefArm64" : "RhpCheckedAssignRef",
                KnownExternFunction.WriteBarrier_EAX => "RhpAssignRefEAX",
                KnownExternFunction.WriteBarrier_EBX => "RhpAssignRefEBX",
                KnownExternFunction.WriteBarrier_ECX => "RhpAssignRefECX",
                KnownExternFunction.WriteBarrier_EDI => "RhpAssignRefEDI",
                KnownExternFunction.WriteBarrier_ESI => "RhpAssignRefESI",
                KnownExternFunction.WriteBarrier_EBP => "RhpAssignRefEBP",
                KnownExternFunction.CheckedWriteBarrier_EAX => "RhpCheckedAssignRefEAX",
                KnownExternFunction.CheckedWriteBarrier_EBX => "RhpCheckedAssignRefEBX",
                KnownExternFunction.CheckedWriteBarrier_ECX => "RhpCheckedAssignRefECX",
                KnownExternFunction.CheckedWriteBarrier_EDI => "RhpCheckedAssignRefEDI",
                KnownExternFunction.CheckedWriteBarrier_ESI => "RhpCheckedAssignRefESI",
                KnownExternFunction.CheckedWriteBarrier_EBP => "RhpCheckedAssignRefEBP",
                KnownExternFunction.BulkMoveWithWriteBarrier => "RhBulkMoveWithWriteBarrier",

                KnownExternFunction.NewArray => "RhNewArray",
                KnownExternFunction.NewObject => "RhNewObject",
                KnownExternFunction.NewFast => "RhpNewFast",
                KnownExternFunction.NewFinalizable => "RhpNewFinalizable",
                KnownExternFunction.NewFastAlign8 => "RhpNewFastAlign8",
                KnownExternFunction.NewFinalizableAlign8 => "RhpNewFinalizableAlign8",
                KnownExternFunction.NewFastMisalign => "RhpNewFastMisalign",
                KnownExternFunction.NewPtrArrayFast => "RhpNewPtrArrayFast",
                KnownExternFunction.NewArrayFastAlign8 => "RhpNewArrayFastAlign8",
                KnownExternFunction.NewArrayFast => "RhpNewArrayFast",

                KnownExternFunction.NativeMemSet => "memset",
                KnownExternFunction.DblRem => "fmod",
                KnownExternFunction.FltRem => "fmodf",

                KnownExternFunction.Lng2Dbl => "RhpLng2Dbl",
                KnownExternFunction.ULng2Dbl => "RhpULng2Dbl",
                KnownExternFunction.Lng2Flt => "RhpLng2Flt",
                KnownExternFunction.ULng2Flt => "RhpULng2Flt",
                KnownExternFunction.Dbl2Lng => "RhpDbl2Lng",
                KnownExternFunction.Dbl2ULng => "RhpDbl2ULng",
                KnownExternFunction.LMul => "RhpLMul",
                KnownExternFunction.LRsz => "RhpLRsz",
                KnownExternFunction.LRsh => "RhpLRsh",
                KnownExternFunction.LLsh => "RhpLLsh",

                KnownExternFunction.PInvokeBegin => "RhpPInvoke",
                KnownExternFunction.PInvokeEnd => "RhpPInvokeReturn",
                KnownExternFunction.ReversePInvokeEnter => "RhpReversePInvoke",
                KnownExternFunction.ReversePInvokeExit => "RhpReversePInvokeReturn",

                KnownExternFunction.GVMLookupForSlot => "RhpDispatchResolve",
                KnownExternFunction.InterfaceDispatch => "RhpInterfaceDispatch",
                KnownExternFunction.InterfaceDispatchGuarded => "RhpInterfaceDispatchGuarded",
                KnownExternFunction.ResolveInterfaceMethodFast => "RhpResolveInterfaceMethodFast",
                KnownExternFunction.ResolveInterfaceMethod => "RhpResolveInterfaceMethod",

                KnownExternFunction.StackProbe => "RhpStackProbe",
                KnownExternFunction.GcPoll => "RhpGcPoll",
                KnownExternFunction.NyiLdVirtFtn => "NYI_LDVIRTFTN",
                KnownExternFunction.TlsGetAddr => "__tls_get_addr",

                _ => throw new NotImplementedException(function.ToString())
            };

            return new Utf8String(name);
        }

        /// <summary>
        /// Gets the signature of <paramref name="function"/>, or null if it has no standard-ABI signature.
        /// </summary>
        /// <remarks>
        /// Extern function nodes are shared by name, so when a function is also reachable through a direct
        /// P/Invoke (e.g. CoreLib's memset), its signature must exactly match that P/Invoke's signature.
        /// </remarks>
        public static ExternalTypeSignature? GetTypeSignature(KnownExternFunction function, TypeSystemContext context)
        {
            TypeDesc voidType = context.GetWellKnownType(WellKnownType.Void);
            TypeDesc voidPointerType = voidType.MakePointerType();
            TypeDesc objectType = context.GetWellKnownType(WellKnownType.Object);
            TypeDesc nativeIntType = context.GetWellKnownType(WellKnownType.IntPtr);
            TypeDesc nativeUIntType = context.GetWellKnownType(WellKnownType.UIntPtr);
            TypeDesc int32Type = context.GetWellKnownType(WellKnownType.Int32);
            TypeDesc int64Type = context.GetWellKnownType(WellKnownType.Int64);
            TypeDesc uint64Type = context.GetWellKnownType(WellKnownType.UInt64);
            TypeDesc singleType = context.GetWellKnownType(WellKnownType.Single);
            TypeDesc doubleType = context.GetWellKnownType(WellKnownType.Double);

            // All of these use the unmanaged calling convention, like the native FCIMPLs that implement most of them.
            ExternalTypeSignature UnmanagedSignature(TypeDesc returnType, params TypeDesc[] parameters) =>
                ExternalTypeSignature.Unmanaged(new MethodSignature(MethodSignatureFlags.Static, 0, returnType, parameters));

            return function switch
            {
                // void RhpThrowEx(object exception) / RhpThrowExact(object exception)
                // INPUT: (RDI on x64 / X0 on arm64 / R0 on arm / A0 on riscv, i.e. the normal first-arg
                // register) is the exception object. See Runtime/amd64/ExceptionHandling.S.
                // Does not return, but is modelled with a void return like the managed ThrowHelpers.
                KnownExternFunction.ThrowEx => UnmanagedSignature(voidType, objectType),
                KnownExternFunction.ThrowExact => UnmanagedSignature(voidType, objectType),

                // void FASTCALL RhpRethrow() - no arguments, uses the currently active ExInfo.
                // See Runtime/amd64/ExceptionHandling.S.
                KnownExternFunction.Rethrow => UnmanagedSignature(voidType),

                // void RhpFallbackFailFast() - Runtime/EHHelpers.cpp: FCIMPL0(void, RhpFallbackFailFast)
                KnownExternFunction.FallbackFailFast => UnmanagedSignature(voidType),

                // void RhDebugBreak() - Runtime/MiscHelpers.cpp: FCIMPL0(void, RhDebugBreak)
                KnownExternFunction.DebugBreak => UnmanagedSignature(voidType),

                // void RhpAssignRef(Object** dst, Object* ref) / RhpCheckedAssignRef(Object** dst, Object* ref)
                // Runtime/portable.cpp: FCIMPL2(void, RhpAssignRef, Object** dst, Object* ref). dst is a raw
                // (non-GC-tracked) pointer to a reference slot, ref is the GC object being stored.
                KnownExternFunction.WriteBarrier => UnmanagedSignature(voidType, nativeIntType, objectType),
                KnownExternFunction.CheckedWriteBarrier => UnmanagedSignature(voidType, nativeIntType, objectType),

                // The EAX/EBX/.../EBP variants are x86-only calling-convention thunks: the destination
                // pointer is implicit in a fixed physical register (no stack slot), and only "ref" is a
                // normal argument. There is no C-callable / standard-ABI signature for these - see the
                // per-register LEAF_ENTRY stubs under Runtime/i386.
                KnownExternFunction.WriteBarrier_EAX or
                KnownExternFunction.WriteBarrier_EBX or
                KnownExternFunction.WriteBarrier_ECX or
                KnownExternFunction.WriteBarrier_EDI or
                KnownExternFunction.WriteBarrier_ESI or
                KnownExternFunction.WriteBarrier_EBP or
                KnownExternFunction.CheckedWriteBarrier_EAX or
                KnownExternFunction.CheckedWriteBarrier_EBX or
                KnownExternFunction.CheckedWriteBarrier_ECX or
                KnownExternFunction.CheckedWriteBarrier_EDI or
                KnownExternFunction.CheckedWriteBarrier_ESI or
                KnownExternFunction.CheckedWriteBarrier_EBP => null,

                // void RhBulkMoveWithWriteBarrier(uint8_t* pDest, uint8_t* pSrc, size_t cbDest) - nativeaot/Runtime/GCMemoryHelpers.cpp.
                // The Wasm JIT expects the same unmanaged (i, i, i) -> void signature (codegenwasm.cpp).
                KnownExternFunction.BulkMoveWithWriteBarrier => UnmanagedSignature(voidType, nativeIntType, nativeIntType, nativeUIntType),

                // object RhNewArray(MethodTable* pEEType, nint length) / object RhNewObject(MethodTable* pEEType) -
                // managed [RuntimeImport] declarations in
                // System.Private.CoreLib/src/System/Runtime/RuntimeImports.cs (RhNewObject/RhNewArray); the
                // array length is nint, matching the fast-path RhpNewArrayFast below.
                // Implemented by managed [RuntimeExport] methods in Runtime.Base/src/System/Runtime/RuntimeExports.cs.
                KnownExternFunction.NewArray => UnmanagedSignature(objectType, nativeIntType, nativeIntType),
                KnownExternFunction.NewObject => UnmanagedSignature(objectType, nativeIntType),

                // Object RhpNewFast(MethodTable* pEEType) et al - nativeaot/Runtime/portable.cpp and
                // runtime/portable/AllocFast.cpp: FCIMPL1(Object*, RhpNewFast, MethodTable* pMT)
                KnownExternFunction.NewFast => UnmanagedSignature(objectType, nativeIntType),
                KnownExternFunction.NewFinalizable => UnmanagedSignature(objectType, nativeIntType),
                KnownExternFunction.NewFastAlign8 => UnmanagedSignature(objectType, nativeIntType),
                KnownExternFunction.NewFinalizableAlign8 => UnmanagedSignature(objectType, nativeIntType),
                KnownExternFunction.NewFastMisalign => UnmanagedSignature(objectType, nativeIntType),

                // Object RhpNewArrayFast(MethodTable* pArrayEEType, intptr_t numElements) et al -
                // runtime/portable/AllocFast.cpp: FCIMPL2(Object*, RhpNewPtrArrayFast, MethodTable* pMT, INT_PTR size)
                // (also FCIMPL2(Array*, RhpNewArrayFast, MethodTable*, intptr_t) in nativeaot/Runtime/portable.cpp).
                KnownExternFunction.NewPtrArrayFast => UnmanagedSignature(objectType, nativeIntType, nativeIntType),
                KnownExternFunction.NewArrayFastAlign8 => UnmanagedSignature(objectType, nativeIntType, nativeIntType),
                KnownExternFunction.NewArrayFast => UnmanagedSignature(objectType, nativeIntType, nativeIntType),

                // void* memset(void* dst, int val, size_t size) - standard libc. Must match the direct P/Invoke
                // `void* memset(void* dest, int value, nuint len)` in System.Private.CoreLib's SpanHelpers.ByteMemOps.cs.
                KnownExternFunction.NativeMemSet => UnmanagedSignature(voidPointerType, voidPointerType, int32Type, nativeUIntType),
                // double fmod(double x, double y) / float fmodf(float x, float y) - standard libc.
                KnownExternFunction.DblRem => UnmanagedSignature(doubleType, doubleType, doubleType),
                KnownExternFunction.FltRem => UnmanagedSignature(singleType, singleType, singleType),

                // Runtime/MathHelpers.cpp: FCIMPL1_L(double, RhpLng2Dbl, int64_t val) et al.
                KnownExternFunction.Lng2Dbl => UnmanagedSignature(doubleType, int64Type),
                KnownExternFunction.ULng2Dbl => UnmanagedSignature(doubleType, uint64Type),
                KnownExternFunction.Lng2Flt => UnmanagedSignature(singleType, int64Type),
                KnownExternFunction.ULng2Flt => UnmanagedSignature(singleType, uint64Type),
                // FCIMPL1_D(int64_t, RhpDbl2Lng, double val) / FCIMPL1_D(uint64_t, RhpDbl2ULng, double val)
                KnownExternFunction.Dbl2Lng => UnmanagedSignature(int64Type, doubleType),
                KnownExternFunction.Dbl2ULng => UnmanagedSignature(uint64Type, doubleType),

                // Runtime/MathHelpers.cpp (HOST_ARM): int64_t RhpLMul(int64_t, int64_t);
                // uint64_t RhpLRsz(uint64_t, int32_t); int64_t RhpLRsh(int64_t, int32_t); int64_t RhpLLsh(int64_t, int32_t).
                KnownExternFunction.LMul => UnmanagedSignature(int64Type, int64Type, int64Type),
                KnownExternFunction.LRsz => UnmanagedSignature(uint64Type, uint64Type, int32Type),
                KnownExternFunction.LRsh => UnmanagedSignature(int64Type, int64Type, int32Type),
                KnownExternFunction.LLsh => UnmanagedSignature(int64Type, int64Type, int32Type),

                // void RhpPInvoke(PInvokeTransitionFrame* pFrame) / RhpPInvokeReturn(PInvokeTransitionFrame* pFrame)
                // "IN: RDI (i.e. the normal first-arg register): address of pinvoke frame" - Runtime/amd64/PInvoke.S.
                // The frame itself is a native-only stack structure, not a GC object, hence IntPtr.
                KnownExternFunction.PInvokeBegin => UnmanagedSignature(voidType, nativeIntType),
                KnownExternFunction.PInvokeEnd => UnmanagedSignature(voidType, nativeIntType),
                // void RhpReversePInvoke(ReversePInvokeFrame* pFrame) / RhpReversePInvokeReturn(...) -
                // Runtime/thread.cpp: FCIMPL1(void, RhpReversePInvoke, ReversePInvokeFrame* pFrame).
                KnownExternFunction.ReversePInvokeEnter => UnmanagedSignature(voidType, nativeIntType),
                KnownExternFunction.ReversePInvokeExit => UnmanagedSignature(voidType, nativeIntType),

                // void RhpGcPoll() - Runtime/portable.cpp: FCIMPL0(void, RhpGcPoll). (RhpGcPoll2, the
                // PInvokeTransitionFrame*-taking variant used on some architectures, is a distinct
                // internal symbol - not the "RhpGcPoll" extern imported here.)
                KnownExternFunction.GcPoll => UnmanagedSignature(voidType),

                // IntPtr RhpDispatchResolve(object pObject, IntPtr pCell) / RhpResolveInterfaceMethod(...) -
                // despite being declared as opaque `EXTERN_C CODE_LOCATION` labels in Runtime/EHHelpers.cpp
                // (used only so the EH code can recognize their addresses for AV-to-NullReferenceException
                // translation), both are genuinely called with the standard ABI: Runtime/amd64/DispatchResolve.S
                // defines RhpDispatchResolve via `INTERFACE_DISPATCH RhpDispatchResolve, 1, rsi` (ReturnTarget=1,
                // i.e. it loads the object's MethodTable from the normal first-arg register (rdi), takes the
                // dispatch cell in the normal second-arg register (rsi), and `ret`s the resolved code pointer in
                // rax -- a plain 2-in/1-out function, unlike the tail-jumping RhpInterfaceDispatch below). This
                // matches the managed RhpResolveInterfaceMethod's confirmed signature (see
                // System.Private.CoreLib/src/System/Runtime/RuntimeImports.cs:
                // "internal static extern IntPtr RhpResolveInterfaceMethod(object pObject, IntPtr pCell);", and
                // its RuntimeExport implementation in Runtime.Base/src/System/Runtime/CachedInterfaceDispatch.cs).
                KnownExternFunction.GVMLookupForSlot => UnmanagedSignature(nativeIntType, objectType, nativeIntType),
                // Implemented by a managed [RuntimeExport] method in Runtime.Base/src/System/Runtime/CachedInterfaceDispatch.cs.
                KnownExternFunction.ResolveInterfaceMethod => UnmanagedSignature(nativeIntType, objectType, nativeIntType),
                // RhpResolveInterfaceMethodFast is no longer defined by the runtime: its asm implementations were
                // removed when interface calls moved to the dispatch helper, and the JIT no longer requests
                // CORINFO_HELP_INTERFACELOOKUP_FOR_SLOT, so there is no signature to describe.
                KnownExternFunction.ResolveInterfaceMethodFast => throw new NotImplementedException(
                    $"{nameof(KnownExternFunction.ResolveInterfaceMethodFast)} is not implemented by the runtime."),

                // RhpInterfaceDispatch[Guarded] are genuine asm-only tail-jump trampolines (see the same
                // Runtime/amd64/DispatchResolve.S macro, `INTERFACE_DISPATCH RhpInterfaceDispatch, 0, r11`):
                // the dispatch cell address is communicated in r11, a register outside the normal argument
                // sequence, and on a cache hit they `jmp` straight to the resolved target without ever
                // returning to their caller in the normal sense. No standard-ABI signature applies.
                KnownExternFunction.InterfaceDispatch or
                KnownExternFunction.InterfaceDispatchGuarded => null,

                // RhpStackProbe is a hand-written asm helper that probes the guard page below a raw stack
                // pointer passed in a fixed physical register; it has no C-callable signature.
                KnownExternFunction.StackProbe => null,

                // NyiLdVirtFtn has no native declaration anywhere in the repo; it is a synthetic
                // fail-fast target name produced by the JIT interface for an unimplemented ldvirtftn path.
                KnownExternFunction.NyiLdVirtFtn => null,

                // __tls_get_addr is the ELF TLS ABI helper (see corinfo.h: "linux/x64 specific"); it is not
                // reachable/meaningful on Wasm, which has its own TLS model.
                KnownExternFunction.TlsGetAddr => null,

                _ => throw new NotImplementedException(function.ToString())
            };
        }
    }
}
