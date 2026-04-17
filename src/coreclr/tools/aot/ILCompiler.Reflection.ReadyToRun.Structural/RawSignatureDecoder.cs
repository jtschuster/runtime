// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using Internal.CorConstants;
using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Structural;

/// <summary>
/// Pure-structural R2R signature decoder that emits a flat stream of <see cref="SignaturePart"/>
/// values, one per raw wire-level field. No AST is built and no metadata or assembly resolution
/// is required. Consumers (e.g., <c>MethodSignature.FromParts</c>, <c>R2RTypeNode.FromParts</c>,
/// <c>R2RFixupSignature.FromParts</c>) walk the same grammar tree as this decoder to reconstruct
/// richer representations.
///
/// The stream is grammar-aware (no explicit begin/end markers); consumers must track their own
/// module-context state by watching <see cref="SignaturePartKind.ModuleZapSigIndex"/>,
/// <see cref="SignaturePartKind.MethodModuleOverride"/>, and
/// <see cref="SignaturePartKind.FixupModuleOverride"/> parts.
/// </summary>
public static class RawSignatureDecoder
{
    // ── Public entry points: materialize a full R2RSignature ─────────────

    public static R2RSignature DecodeMethodSignature(NativeReader reader, int offset, int targetPointerSize)
        => Materialize(reader, offset, targetPointerSize, ctx => ctx.EmitMethod());

    public static R2RSignature DecodeTypeSignature(NativeReader reader, int offset, int targetPointerSize)
        => Materialize(reader, offset, targetPointerSize, ctx => ctx.EmitType());

    public static R2RSignature DecodeFieldSignature(NativeReader reader, int offset, int targetPointerSize)
        => Materialize(reader, offset, targetPointerSize, ctx => ctx.EmitField());

    public static R2RSignature DecodeFixupSignature(NativeReader reader, int offset, int targetPointerSize)
        => Materialize(reader, offset, targetPointerSize, ctx => ctx.EmitFixup());

    // ── Public enumerators: lazy IEnumerable<SignaturePart> ──────────────

    public static IEnumerable<SignaturePart> EnumerateMethodParts(NativeReader reader, int offset, int targetPointerSize)
        => new Context(reader, offset, targetPointerSize).EmitMethod();

    public static IEnumerable<SignaturePart> EnumerateTypeParts(NativeReader reader, int offset, int targetPointerSize)
        => new Context(reader, offset, targetPointerSize).EmitType();

    public static IEnumerable<SignaturePart> EnumerateFieldParts(NativeReader reader, int offset, int targetPointerSize)
        => new Context(reader, offset, targetPointerSize).EmitField();

    public static IEnumerable<SignaturePart> EnumerateFixupParts(NativeReader reader, int offset, int targetPointerSize)
        => new Context(reader, offset, targetPointerSize).EmitFixup();

    // ── Materialization helper ───────────────────────────────────────────

    private static R2RSignature Materialize(NativeReader reader, int offset, int targetPointerSize, Func<Context, IEnumerable<SignaturePart>> emit)
    {
        var ctx = new Context(reader, offset, targetPointerSize);
        var builder = ImmutableArray.CreateBuilder<SignaturePart>();
        foreach (var part in emit(ctx))
            builder.Add(part);
        return new R2RSignature(builder.ToImmutable(), offset, ctx.Offset);
    }

    // ── Stateful decode context (mutated during yield return execution) ──

    private sealed class Context
    {
        private readonly NativeReader _reader;
        private readonly int _targetPointerSize;
        private int _offset;

        public int Offset => _offset;

        public Context(NativeReader reader, int offset, int targetPointerSize)
        {
            _reader = reader;
            _offset = offset;
            _targetPointerSize = targetPointerSize;
        }

        // ── Byte-reading primitives ──────────────────────────────────────

        private byte ReadByte() => _reader.ReadByte(ref _offset);

        private uint ReadUInt() => _reader.ReadCompressedData(ref _offset);

        private int ReadInt()
        {
            uint raw = ReadUInt();
            int data = (int)(raw >> 1);
            return (raw & 1) == 0 ? data : -data;
        }

        private CorElementType ReadElementType() => (CorElementType)(ReadByte() & 0x7F);

        private CorElementType PeekElementType()
        {
            int peekOffset = _offset;
            return (CorElementType)(_reader.ReadByte(ref peekOffset) & 0x7F);
        }

        // ── Type signature ───────────────────────────────────────────────

        public IEnumerable<SignaturePart> EmitType()
        {
            var elemType = ReadElementType();
            yield return new SignaturePart(SignaturePartKind.ElementType, (long)elemType);

            switch (elemType)
            {
                case CorElementType.ELEMENT_TYPE_VOID:
                case CorElementType.ELEMENT_TYPE_BOOLEAN:
                case CorElementType.ELEMENT_TYPE_CHAR:
                case CorElementType.ELEMENT_TYPE_I1:
                case CorElementType.ELEMENT_TYPE_U1:
                case CorElementType.ELEMENT_TYPE_I2:
                case CorElementType.ELEMENT_TYPE_U2:
                case CorElementType.ELEMENT_TYPE_I4:
                case CorElementType.ELEMENT_TYPE_U4:
                case CorElementType.ELEMENT_TYPE_I8:
                case CorElementType.ELEMENT_TYPE_U8:
                case CorElementType.ELEMENT_TYPE_R4:
                case CorElementType.ELEMENT_TYPE_R8:
                case CorElementType.ELEMENT_TYPE_STRING:
                case CorElementType.ELEMENT_TYPE_OBJECT:
                case CorElementType.ELEMENT_TYPE_I:
                case CorElementType.ELEMENT_TYPE_U:
                case CorElementType.ELEMENT_TYPE_TYPEDBYREF:
                case CorElementType.ELEMENT_TYPE_CANON_ZAPSIG:
                    yield break;

                case CorElementType.ELEMENT_TYPE_PTR:
                case CorElementType.ELEMENT_TYPE_BYREF:
                case CorElementType.ELEMENT_TYPE_SZARRAY:
                case CorElementType.ELEMENT_TYPE_PINNED:
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;

                case CorElementType.ELEMENT_TYPE_VALUETYPE:
                case CorElementType.ELEMENT_TYPE_CLASS:
                    yield return new SignaturePart(SignaturePartKind.TypeToken, ReadUInt());
                    yield break;

                case CorElementType.ELEMENT_TYPE_VAR:
                case CorElementType.ELEMENT_TYPE_MVAR:
                    yield return new SignaturePart(SignaturePartKind.GenericParamIndex, ReadUInt());
                    yield break;

                case CorElementType.ELEMENT_TYPE_ARRAY:
                {
                    foreach (var p in EmitType())
                        yield return p;
                    uint rank = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.ArrayRank, rank);
                    // Wire format: if rank == 0, no size/lower-bound data follows
                    // (decoder treats rank 0 as SzArray shape).
                    if (rank == 0)
                        yield break;
                    uint sizeCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.ArraySizeCount, sizeCount);
                    for (uint i = 0; i < sizeCount; i++)
                        yield return new SignaturePart(SignaturePartKind.ArraySize, ReadUInt());
                    uint lbCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.ArrayLowerBoundCount, lbCount);
                    for (uint i = 0; i < lbCount; i++)
                        yield return new SignaturePart(SignaturePartKind.ArrayLowerBound, ReadInt());
                    yield break;
                }

                case CorElementType.ELEMENT_TYPE_GENERICINST:
                {
                    foreach (var p in EmitType())
                        yield return p;
                    uint argCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.GenericInstArgCount, argCount);
                    for (uint i = 0; i < argCount; i++)
                        foreach (var p in EmitType())
                            yield return p;
                    yield break;
                }

                case CorElementType.ELEMENT_TYPE_FNPTR:
                {
                    byte sigHeader = ReadByte();
                    yield return new SignaturePart(SignaturePartKind.FnPtrSigHeader, sigHeader);

                    var header = new System.Reflection.Metadata.SignatureHeader(sigHeader);
                    if (header.IsGeneric)
                        yield return new SignaturePart(SignaturePartKind.FnPtrGenericParamCount, ReadUInt());

                    uint paramCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.FnPtrParamCount, paramCount);

                    // Return type
                    foreach (var p in EmitType())
                        yield return p;

                    for (uint i = 0; i < paramCount; i++)
                    {
                        while (PeekElementType() == CorElementType.ELEMENT_TYPE_SENTINEL)
                        {
                            ReadElementType();
                            yield return new SignaturePart(SignaturePartKind.FnPtrSentinel, 0);
                        }
                        foreach (var p in EmitType())
                            yield return p;
                    }
                    yield break;
                }

                case CorElementType.ELEMENT_TYPE_CMOD_REQD:
                case CorElementType.ELEMENT_TYPE_CMOD_OPT:
                    yield return new SignaturePart(SignaturePartKind.TypeToken, ReadUInt());
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;

                case CorElementType.ELEMENT_TYPE_MODULE_ZAPSIG:
                {
                    yield return new SignaturePart(SignaturePartKind.ModuleZapSigIndex, ReadUInt());
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;
                }

                case CorElementType.ELEMENT_TYPE_VAR_ZAPSIG:
                    throw new BadImageFormatException("ELEMENT_TYPE_VAR_ZAPSIG not supported in structural decoder");

                case CorElementType.ELEMENT_TYPE_NATIVE_VALUETYPE_ZAPSIG:
                    throw new BadImageFormatException("ELEMENT_TYPE_NATIVE_VALUETYPE_ZAPSIG not supported in structural decoder");

                default:
                    throw new BadImageFormatException($"Unexpected element type: 0x{(byte)elemType:X2}");
            }
        }

        // ── Method signature ─────────────────────────────────────────────

        public IEnumerable<SignaturePart> EmitMethod()
        {
            uint flags = ReadUInt();
            yield return new SignaturePart(SignaturePartKind.MethodFlags, flags);

            if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UpdateContext) != 0)
                yield return new SignaturePart(SignaturePartKind.MethodModuleOverride, ReadUInt());

            if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType) != 0)
                foreach (var p in EmitType())
                    yield return p;

            if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_SlotInsteadOfToken) != 0)
                throw new NotImplementedException("SlotInsteadOfToken");

            yield return new SignaturePart(SignaturePartKind.MethodRid, ReadUInt());

            if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation) != 0)
            {
                uint argCount = ReadUInt();
                yield return new SignaturePart(SignaturePartKind.MethodTypeArgCount, argCount);
                for (uint i = 0; i < argCount; i++)
                    foreach (var p in EmitType())
                        yield return p;
            }

            if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained) != 0)
                foreach (var p in EmitType())
                    yield return p;
        }

        // ── Field signature ──────────────────────────────────────────────

        public IEnumerable<SignaturePart> EmitField()
        {
            uint flags = ReadUInt();
            yield return new SignaturePart(SignaturePartKind.FieldFlags, flags);

            if ((flags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_OwnerType) != 0)
                foreach (var p in EmitType())
                    yield return p;

            yield return new SignaturePart(SignaturePartKind.FieldRid, ReadUInt());
        }

        // ── Fixup signature ──────────────────────────────────────────────

        public IEnumerable<SignaturePart> EmitFixup()
        {
            byte fixupByte = ReadByte();
            yield return new SignaturePart(SignaturePartKind.FixupKindByte, fixupByte);

            bool moduleOverride = (fixupByte & (byte)ReadyToRunFixupKind.ModuleOverride) != 0;
            var fixupKind = (ReadyToRunFixupKind)(fixupByte & ~(byte)ReadyToRunFixupKind.ModuleOverride);

            if (moduleOverride)
                yield return new SignaturePart(SignaturePartKind.FixupModuleOverride, ReadUInt());

            foreach (var p in EmitFixupPayload(fixupKind))
                yield return p;
        }

        private IEnumerable<SignaturePart> EmitFixupPayload(ReadyToRunFixupKind fixupKind)
        {
            switch (fixupKind)
            {
                case ReadyToRunFixupKind.ThisObjDictionaryLookup:
                    foreach (var p in EmitType())
                        yield return p;
                    foreach (var p in EmitFixup())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.TypeDictionaryLookup:
                case ReadyToRunFixupKind.MethodDictionaryLookup:
                    foreach (var p in EmitFixup())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.TypeHandle:
                case ReadyToRunFixupKind.NewObject:
                case ReadyToRunFixupKind.NewArray:
                case ReadyToRunFixupKind.IsInstanceOf:
                case ReadyToRunFixupKind.ChkCast:
                case ReadyToRunFixupKind.CctorTrigger:
                case ReadyToRunFixupKind.StaticBaseNonGC:
                case ReadyToRunFixupKind.StaticBaseGC:
                case ReadyToRunFixupKind.ThreadStaticBaseNonGC:
                case ReadyToRunFixupKind.ThreadStaticBaseGC:
                case ReadyToRunFixupKind.FieldBaseOffset:
                case ReadyToRunFixupKind.TypeDictionary:
                case ReadyToRunFixupKind.DeclaringTypeHandle:
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.MethodHandle:
                case ReadyToRunFixupKind.MethodEntry:
                case ReadyToRunFixupKind.VirtualEntry:
                case ReadyToRunFixupKind.MethodDictionary:
                case ReadyToRunFixupKind.IndirectPInvokeTarget:
                case ReadyToRunFixupKind.PInvokeTarget:
                    foreach (var p in EmitMethod())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.FieldHandle:
                case ReadyToRunFixupKind.FieldAddress:
                case ReadyToRunFixupKind.FieldOffset:
                    foreach (var p in EmitField())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.MethodEntry_DefToken:
                case ReadyToRunFixupKind.VirtualEntry_DefToken:
                case ReadyToRunFixupKind.MethodEntry_RefToken:
                case ReadyToRunFixupKind.VirtualEntry_RefToken:
                    yield return new SignaturePart(SignaturePartKind.MethodRid, ReadUInt());
                    yield break;

                case ReadyToRunFixupKind.VirtualEntry_Slot:
                    yield return new SignaturePart(SignaturePartKind.VirtualSlotIndex, ReadUInt());
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.Helper:
                    yield return new SignaturePart(SignaturePartKind.HelperId, ReadUInt());
                    yield break;

                case ReadyToRunFixupKind.StringHandle:
                    yield return new SignaturePart(SignaturePartKind.UserStringToken, ReadUInt());
                    yield break;

                case ReadyToRunFixupKind.Check_TypeLayout:
                case ReadyToRunFixupKind.Verify_TypeLayout:
                case ReadyToRunFixupKind.ContinuationLayout:
                    foreach (var p in EmitType())
                        yield return p;
                    foreach (var p in EmitTypeLayoutPayload())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.ResumptionStubEntryPoint:
                    yield return new SignaturePart(SignaturePartKind.ResumptionStubRva, _reader.ReadInt32(ref _offset));
                    yield break;

                case ReadyToRunFixupKind.Check_VirtualFunctionOverride:
                case ReadyToRunFixupKind.Verify_VirtualFunctionOverride:
                {
                    uint flags = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.VirtualOverrideFlags, flags);
                    foreach (var p in EmitMethod())
                        yield return p;
                    foreach (var p in EmitType())
                        yield return p;
                    if (((ReadyToRunVirtualFunctionOverrideFlags)flags)
                        .HasFlag(ReadyToRunVirtualFunctionOverrideFlags.VirtualFunctionOverridden))
                    {
                        foreach (var p in EmitMethod())
                            yield return p;
                    }
                    yield break;
                }

                case ReadyToRunFixupKind.Check_FieldOffset:
                    yield return new SignaturePart(SignaturePartKind.FieldExpectedOffset, ReadUInt());
                    foreach (var p in EmitField())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.Verify_FieldOffset:
                    yield return new SignaturePart(SignaturePartKind.FieldExpectedOffset, ReadUInt());
                    yield return new SignaturePart(SignaturePartKind.VerifyFieldOffsetSecond, ReadUInt());
                    foreach (var p in EmitField())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.Check_InstructionSetSupport:
                {
                    uint count = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.InstructionSetCount, count);
                    for (uint i = 0; i < count; i++)
                        yield return new SignaturePart(SignaturePartKind.InstructionSetEncoded, ReadUInt());
                    yield break;
                }

                case ReadyToRunFixupKind.DelegateCtor:
                    foreach (var p in EmitMethod())
                        yield return p;
                    foreach (var p in EmitType())
                        yield return p;
                    yield break;

                case ReadyToRunFixupKind.Check_IL_Body:
                case ReadyToRunFixupKind.Verify_IL_Body:
                {
                    uint ilByteCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.ILBodyByteCount, ilByteCount);
                    byte[] ilBytes = new byte[ilByteCount];
                    for (uint i = 0; i < ilByteCount; i++)
                        ilBytes[i] = ReadByte();
                    yield return new SignaturePart(SignaturePartKind.ILBodyBlob, ilBytes);

                    uint typeCount = ReadUInt();
                    yield return new SignaturePart(SignaturePartKind.ILBodyTypeCount, typeCount);
                    for (uint i = 0; i < typeCount; i++)
                        foreach (var p in EmitType())
                            yield return p;

                    foreach (var p in EmitMethod())
                        yield return p;
                    yield break;
                }

                default:
                    yield break;
            }
        }

        private IEnumerable<SignaturePart> EmitTypeLayoutPayload()
        {
            uint flags = ReadUInt();
            yield return new SignaturePart(SignaturePartKind.TypeLayoutFlags, flags);

            uint size = ReadUInt();
            yield return new SignaturePart(SignaturePartKind.TypeLayoutSize, size);

            var layoutFlags = (ReadyToRunTypeLayoutFlags)flags;

            if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_HFA))
                yield return new SignaturePart(SignaturePartKind.TypeLayoutHfaType, ReadByte());

            if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment)
                && !layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment_Native))
            {
                yield return new SignaturePart(SignaturePartKind.TypeLayoutAlignment, ReadUInt());
            }

            if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout)
                && !layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout_Empty))
            {
                int cbGCRefMap = ((int)size / _targetPointerSize + 7) / 8;
                byte[] blob = new byte[cbGCRefMap];
                for (int i = 0; i < cbGCRefMap; i++)
                    blob[i] = ReadByte();
                yield return new SignaturePart(SignaturePartKind.TypeLayoutGcRefBlob, blob);
            }
        }
    }
}
