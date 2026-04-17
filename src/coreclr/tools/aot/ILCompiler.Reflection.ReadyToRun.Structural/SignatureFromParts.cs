// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;

using Internal.CorConstants;
using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Structural;

/// <summary>
/// Factory methods that build AST nodes (<see cref="MethodSignature"/>, <see cref="R2RTypeNode"/>,
/// <see cref="R2RFieldRef"/>, <see cref="R2RFixupSignature"/>) from a flat <see cref="SignaturePart"/>
/// stream produced by <see cref="RawSignatureDecoder"/>.
///
/// Consumers walk the same grammar tree as the decoder, advancing <c>ref int index</c> through
/// the stream. Module context is replayed by watching
/// <see cref="SignaturePartKind.ModuleZapSigIndex"/>,
/// <see cref="SignaturePartKind.MethodModuleOverride"/>, and
/// <see cref="SignaturePartKind.FixupModuleOverride"/> parts.
/// </summary>
public sealed partial class MethodSignature
{
    public static MethodSignature FromSignature(R2RSignature signature, int contextModule = -1)
    {
        int index = 0;
        return FromParts(signature.Parts, ref index, contextModule);
    }

    public static MethodSignature FromParts(ImmutableArray<SignaturePart> parts, ref int index, int contextModule)
    {
        uint flags = (uint)Expect(parts, ref index, SignaturePartKind.MethodFlags);

        int moduleIndex = contextModule;
        if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UpdateContext) != 0)
        {
            moduleIndex = (int)Expect(parts, ref index, SignaturePartKind.MethodModuleOverride);
        }

        R2RTypeNode ownerType = null;
        if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType) != 0)
        {
            ownerType = R2RTypeNode.FromParts(parts, ref index, moduleIndex);
        }

        bool isMemberRef = (flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MemberRefToken) != 0;
        int rid = (int)Expect(parts, ref index, SignaturePartKind.MethodRid);

        ImmutableArray<R2RTypeNode> typeArgs = ImmutableArray<R2RTypeNode>.Empty;
        if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation) != 0)
        {
            uint argCount = (uint)Expect(parts, ref index, SignaturePartKind.MethodTypeArgCount);
            var builder = ImmutableArray.CreateBuilder<R2RTypeNode>((int)argCount);
            for (uint i = 0; i < argCount; i++)
                builder.Add(R2RTypeNode.FromParts(parts, ref index, moduleIndex));
            typeArgs = builder.MoveToImmutable();
        }

        R2RTypeNode constrainedType = null;
        if ((flags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained) != 0)
        {
            constrainedType = R2RTypeNode.FromParts(parts, ref index, moduleIndex);
        }

        var prefixFlags = (ReadyToRunMethodSigFlags)(flags & (uint)(
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub |
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub |
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant));

        return new MethodSignature(prefixFlags, moduleIndex, ownerType, isMemberRef, rid, typeArgs, constrainedType);
    }

    internal static long Expect(ImmutableArray<SignaturePart> parts, ref int index, SignaturePartKind kind)
    {
        if (index >= parts.Length)
            throw new BadImageFormatException($"Unexpected end of signature stream; expected {kind}");
        if (parts[index].Kind != kind)
            throw new BadImageFormatException($"Expected {kind} at index {index}, got {parts[index].Kind}");
        return parts[index++].Value;
    }

    internal static byte[] ExpectBlob(ImmutableArray<SignaturePart> parts, ref int index, SignaturePartKind kind)
    {
        if (index >= parts.Length)
            throw new BadImageFormatException($"Unexpected end of signature stream; expected {kind}");
        if (parts[index].Kind != kind)
            throw new BadImageFormatException($"Expected {kind} at index {index}, got {parts[index].Kind}");
        return parts[index++].Blob;
    }
}

public abstract partial class R2RTypeNode
{
    public static R2RTypeNode FromSignature(R2RSignature signature, int contextModule = -1)
    {
        int index = 0;
        return FromParts(signature.Parts, ref index, contextModule);
    }

    public static R2RTypeNode FromParts(ImmutableArray<SignaturePart> parts, ref int index, int contextModule)
    {
        var elemType = (CorElementType)MethodSignature.Expect(parts, ref index, SignaturePartKind.ElementType);

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
                return new R2RPrimitiveTypeNode((PrimitiveTypeCode)elemType);

            case CorElementType.ELEMENT_TYPE_PTR:
                return new R2RPointerTypeNode(FromParts(parts, ref index, contextModule));

            case CorElementType.ELEMENT_TYPE_BYREF:
                return new R2RByRefTypeNode(FromParts(parts, ref index, contextModule));

            case CorElementType.ELEMENT_TYPE_VALUETYPE:
            case CorElementType.ELEMENT_TYPE_CLASS:
            {
                bool isValueType = elemType == CorElementType.ELEMENT_TYPE_VALUETYPE;
                uint encoded = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeToken);
                var (kind, rid) = DecodeTypeToken(encoded);
                return new R2RTokenTypeNode(contextModule, kind, rid, isValueType);
            }

            case CorElementType.ELEMENT_TYPE_VAR:
                return new R2RGenericTypeParamNode((int)MethodSignature.Expect(parts, ref index, SignaturePartKind.GenericParamIndex));

            case CorElementType.ELEMENT_TYPE_MVAR:
                return new R2RGenericMethodParamNode((int)MethodSignature.Expect(parts, ref index, SignaturePartKind.GenericParamIndex));

            case CorElementType.ELEMENT_TYPE_ARRAY:
            {
                R2RTypeNode elementType = FromParts(parts, ref index, contextModule);
                uint rank = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.ArrayRank);
                if (rank == 0)
                    return new R2RSzArrayTypeNode(elementType);

                uint sizeCount = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.ArraySizeCount);
                var sizes = ImmutableArray.CreateBuilder<int>((int)sizeCount);
                for (uint i = 0; i < sizeCount; i++)
                    sizes.Add((int)MethodSignature.Expect(parts, ref index, SignaturePartKind.ArraySize));

                uint lbCount = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.ArrayLowerBoundCount);
                var lbs = ImmutableArray.CreateBuilder<int>((int)lbCount);
                for (uint i = 0; i < lbCount; i++)
                    lbs.Add((int)MethodSignature.Expect(parts, ref index, SignaturePartKind.ArrayLowerBound));

                var shape = new ArrayShape((int)rank, sizes.MoveToImmutable(), lbs.MoveToImmutable());
                return new R2RArrayTypeNode(elementType, shape);
            }

            case CorElementType.ELEMENT_TYPE_SZARRAY:
                return new R2RSzArrayTypeNode(FromParts(parts, ref index, contextModule));

            case CorElementType.ELEMENT_TYPE_GENERICINST:
            {
                R2RTypeNode genericType = FromParts(parts, ref index, contextModule);
                uint argCount = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.GenericInstArgCount);
                var args = ImmutableArray.CreateBuilder<R2RTypeNode>((int)argCount);
                for (uint i = 0; i < argCount; i++)
                    args.Add(FromParts(parts, ref index, contextModule));
                return new R2RGenericInstTypeNode(genericType, args.MoveToImmutable());
            }

            case CorElementType.ELEMENT_TYPE_FNPTR:
            {
                byte headerByte = (byte)MethodSignature.Expect(parts, ref index, SignaturePartKind.FnPtrSigHeader);
                var header = new SignatureHeader(headerByte);
                int genericParamCount = 0;
                if (header.IsGeneric)
                    genericParamCount = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.FnPtrGenericParamCount);
                int paramCount = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.FnPtrParamCount);
                R2RTypeNode returnType = FromParts(parts, ref index, contextModule);

                var paramTypes = ImmutableArray.CreateBuilder<R2RTypeNode>(paramCount);
                int requiredParamCount = -1;
                for (int i = 0; i < paramCount; i++)
                {
                    while (index < parts.Length && parts[index].Kind == SignaturePartKind.FnPtrSentinel)
                    {
                        requiredParamCount = i;
                        index++;
                    }
                    paramTypes.Add(FromParts(parts, ref index, contextModule));
                }
                if (requiredParamCount == -1)
                    requiredParamCount = paramCount;

                var methodSig = new MethodSignature<R2RTypeNode>(header, returnType, requiredParamCount, genericParamCount, paramTypes.MoveToImmutable());
                return new R2RFunctionPointerTypeNode(methodSig);
            }

            case CorElementType.ELEMENT_TYPE_CMOD_REQD:
            case CorElementType.ELEMENT_TYPE_CMOD_OPT:
            {
                uint encoded = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeToken);
                var (kind, rid) = DecodeTypeToken(encoded);
                R2RTypeNode modifier = new R2RTokenTypeNode(contextModule, kind, rid, false);
                R2RTypeNode inner = FromParts(parts, ref index, contextModule);
                return new R2RModifiedTypeNode(modifier, inner, isRequired: elemType == CorElementType.ELEMENT_TYPE_CMOD_REQD);
            }

            case CorElementType.ELEMENT_TYPE_PINNED:
                return new R2RPinnedTypeNode(FromParts(parts, ref index, contextModule));

            case CorElementType.ELEMENT_TYPE_CANON_ZAPSIG:
                return R2RCanonTypeNode.Instance;

            case CorElementType.ELEMENT_TYPE_MODULE_ZAPSIG:
            {
                int moduleIndex = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.ModuleZapSigIndex);
                R2RTypeNode inner = FromParts(parts, ref index, moduleIndex);
                return new R2RModuleQualifiedTypeNode(moduleIndex, inner);
            }

            default:
                throw new BadImageFormatException($"Unexpected element type: 0x{(byte)elemType:X2}");
        }
    }

    private static (HandleKind kind, int rid) DecodeTypeToken(uint encoded)
    {
        int rid = (int)(encoded >> 2);
        HandleKind kind = (encoded & 3) switch
        {
            0 => HandleKind.TypeDefinition,
            1 => HandleKind.TypeReference,
            2 => HandleKind.TypeSpecification,
            3 => HandleKind.TypeDefinition,
            _ => throw new BadImageFormatException()
        };
        return (kind, rid);
    }
}

public sealed partial class R2RFieldRef
{
    public static R2RFieldRef FromSignature(R2RSignature signature, int contextModule = -1)
    {
        int index = 0;
        return FromParts(signature.Parts, ref index, contextModule);
    }

    public static R2RFieldRef FromParts(ImmutableArray<SignaturePart> parts, ref int index, int contextModule)
    {
        uint flags = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.FieldFlags);

        R2RTypeNode ownerType = null;
        if ((flags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_OwnerType) != 0)
        {
            ownerType = R2RTypeNode.FromParts(parts, ref index, contextModule);
        }

        bool isMemberRef = (flags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_MemberRefToken) != 0;
        int rid = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.FieldRid);

        return new R2RFieldRef((ReadyToRunFieldSigFlags)flags, ownerType, isMemberRef, rid);
    }
}

public sealed partial class R2RFixupSignature
{
    public static R2RFixupSignature FromSignature(R2RSignature signature, int contextModule = -1)
    {
        int index = 0;
        return FromParts(signature.Parts, ref index, contextModule);
    }

    public static R2RFixupSignature FromParts(ImmutableArray<SignaturePart> parts, ref int index, int contextModule)
    {
        byte fixupByte = (byte)MethodSignature.Expect(parts, ref index, SignaturePartKind.FixupKindByte);
        bool hasModuleOverride = (fixupByte & (byte)ReadyToRunFixupKind.ModuleOverride) != 0;
        var fixupKind = (ReadyToRunFixupKind)(fixupByte & ~(byte)ReadyToRunFixupKind.ModuleOverride);

        int moduleIndex = -1;
        int payloadContext = contextModule;
        if (hasModuleOverride)
        {
            moduleIndex = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.FixupModuleOverride);
            payloadContext = moduleIndex;
        }

        R2RFixupPayload payload = BuildPayload(parts, ref index, fixupKind, payloadContext);
        return new R2RFixupSignature(fixupKind, moduleIndex, payload);
    }

    private static R2RFixupPayload BuildPayload(ImmutableArray<SignaturePart> parts, ref int index, ReadyToRunFixupKind fixupKind, int contextModule)
    {
        switch (fixupKind)
        {
            case ReadyToRunFixupKind.ThisObjDictionaryLookup:
            {
                R2RTypeNode lookupType = R2RTypeNode.FromParts(parts, ref index, contextModule);
                R2RFixupSignature inner = FromParts(parts, ref index, contextModule);
                return new R2RDictionaryLookupFixupPayload(lookupType, inner);
            }

            case ReadyToRunFixupKind.TypeDictionaryLookup:
            case ReadyToRunFixupKind.MethodDictionaryLookup:
            {
                R2RFixupSignature inner = FromParts(parts, ref index, contextModule);
                return new R2RDictionaryLookupFixupPayload(null, inner);
            }

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
                return new R2RTypeFixupPayload(R2RTypeNode.FromParts(parts, ref index, contextModule));

            case ReadyToRunFixupKind.MethodHandle:
            case ReadyToRunFixupKind.MethodEntry:
            case ReadyToRunFixupKind.VirtualEntry:
            case ReadyToRunFixupKind.MethodDictionary:
            case ReadyToRunFixupKind.IndirectPInvokeTarget:
            case ReadyToRunFixupKind.PInvokeTarget:
                return new R2RMethodFixupPayload(MethodSignature.FromParts(parts, ref index, contextModule));

            case ReadyToRunFixupKind.FieldHandle:
            case ReadyToRunFixupKind.FieldAddress:
                return new R2RFieldFixupPayload(R2RFieldRef.FromParts(parts, ref index, contextModule));

            case ReadyToRunFixupKind.FieldOffset:
                return new R2RFieldOffsetValueFixupPayload(R2RFieldRef.FromParts(parts, ref index, contextModule));

            case ReadyToRunFixupKind.MethodEntry_DefToken:
            case ReadyToRunFixupKind.VirtualEntry_DefToken:
                return new R2RTokenFixupPayload(isMemberRef: false,
                    (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.MethodRid));

            case ReadyToRunFixupKind.MethodEntry_RefToken:
            case ReadyToRunFixupKind.VirtualEntry_RefToken:
                return new R2RTokenFixupPayload(isMemberRef: true,
                    (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.MethodRid));

            case ReadyToRunFixupKind.VirtualEntry_Slot:
            {
                int slot = (int)MethodSignature.Expect(parts, ref index, SignaturePartKind.VirtualSlotIndex);
                R2RTypeNode type = R2RTypeNode.FromParts(parts, ref index, contextModule);
                return new R2RSlotFixupPayload(slot, type);
            }

            case ReadyToRunFixupKind.Helper:
                return new R2RHelperFixupPayload((ReadyToRunHelper)
                    MethodSignature.Expect(parts, ref index, SignaturePartKind.HelperId));

            case ReadyToRunFixupKind.StringHandle:
                return new R2RStringFixupPayload((int)
                    MethodSignature.Expect(parts, ref index, SignaturePartKind.UserStringToken));

            case ReadyToRunFixupKind.Check_TypeLayout:
            case ReadyToRunFixupKind.Verify_TypeLayout:
            case ReadyToRunFixupKind.ContinuationLayout:
            {
                R2RTypeNode type = R2RTypeNode.FromParts(parts, ref index, contextModule);
                return BuildTypeLayoutPayload(parts, ref index, type);
            }

            case ReadyToRunFixupKind.ResumptionStubEntryPoint:
                return new R2RStubEntryPointFixupPayload((int)
                    MethodSignature.Expect(parts, ref index, SignaturePartKind.ResumptionStubRva));

            case ReadyToRunFixupKind.Check_VirtualFunctionOverride:
            case ReadyToRunFixupKind.Verify_VirtualFunctionOverride:
            {
                uint flags = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.VirtualOverrideFlags);
                MethodSignature declaration = MethodSignature.FromParts(parts, ref index, contextModule);
                R2RTypeNode implType = R2RTypeNode.FromParts(parts, ref index, contextModule);
                MethodSignature implMethod = null;
                if (((ReadyToRunVirtualFunctionOverrideFlags)flags)
                    .HasFlag(ReadyToRunVirtualFunctionOverrideFlags.VirtualFunctionOverridden))
                {
                    implMethod = MethodSignature.FromParts(parts, ref index, contextModule);
                }
                return new R2RVirtualOverrideFixupPayload(flags, declaration, implType, declaringTypeHandle: null, implMethod);
            }

            case ReadyToRunFixupKind.Check_FieldOffset:
            {
                uint expected = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.FieldExpectedOffset);
                R2RFieldRef field = R2RFieldRef.FromParts(parts, ref index, contextModule);
                return new R2RFieldOffsetFixupPayload(expected, field);
            }

            case ReadyToRunFixupKind.Verify_FieldOffset:
            {
                uint expected = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.FieldExpectedOffset);
                MethodSignature.Expect(parts, ref index, SignaturePartKind.VerifyFieldOffsetSecond);
                R2RFieldRef field = R2RFieldRef.FromParts(parts, ref index, contextModule);
                return new R2RFieldOffsetFixupPayload(expected, field);
            }

            case ReadyToRunFixupKind.Check_InstructionSetSupport:
            {
                uint count = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.InstructionSetCount);
                var supported = ImmutableArray.CreateBuilder<ushort>();
                var unsupported = ImmutableArray.CreateBuilder<ushort>();
                for (uint i = 0; i < count; i++)
                {
                    uint encoded = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.InstructionSetEncoded);
                    ushort set = (ushort)(encoded >> 1);
                    if ((encoded & 1) == 1)
                        supported.Add(set);
                    else
                        unsupported.Add(set);
                }
                return new R2RInstructionSetFixupPayload(supported.ToImmutable(), unsupported.ToImmutable());
            }

            case ReadyToRunFixupKind.DelegateCtor:
            {
                MethodSignature target = MethodSignature.FromParts(parts, ref index, contextModule);
                R2RTypeNode delegateType = R2RTypeNode.FromParts(parts, ref index, contextModule);
                return new R2RDelegateCtorFixupPayload(target, delegateType);
            }

            case ReadyToRunFixupKind.Check_IL_Body:
            case ReadyToRunFixupKind.Verify_IL_Body:
            {
                MethodSignature.Expect(parts, ref index, SignaturePartKind.ILBodyByteCount);
                byte[] ilBytes = MethodSignature.ExpectBlob(parts, ref index, SignaturePartKind.ILBodyBlob);
                uint typeCount = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.ILBodyTypeCount);
                var types = ImmutableArray.CreateBuilder<R2RTypeNode>((int)typeCount);
                for (uint i = 0; i < typeCount; i++)
                    types.Add(R2RTypeNode.FromParts(parts, ref index, contextModule));
                MethodSignature method = MethodSignature.FromParts(parts, ref index, contextModule);
                return new R2RILBodyFixupPayload(method, (ilBytes ?? Array.Empty<byte>()).ToImmutableArray(), types.MoveToImmutable());
            }

            default:
                return R2REmptyFixupPayload.Instance;
        }
    }

    private static R2RTypeLayoutFixupPayload BuildTypeLayoutPayload(ImmutableArray<SignaturePart> parts, ref int index, R2RTypeNode type)
    {
        uint flags = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeLayoutFlags);
        var layoutFlags = (ReadyToRunTypeLayoutFlags)flags;

        uint size = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeLayoutSize);

        byte hfaType = 0;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_HFA))
            hfaType = (byte)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeLayoutHfaType);

        uint alignment = 0;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment)
            && !layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment_Native))
        {
            alignment = (uint)MethodSignature.Expect(parts, ref index, SignaturePartKind.TypeLayoutAlignment);
        }

        var gcLayout = ImmutableArray<byte>.Empty;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout)
            && !layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout_Empty))
        {
            byte[] blob = MethodSignature.ExpectBlob(parts, ref index, SignaturePartKind.TypeLayoutGcRefBlob);
            gcLayout = (blob ?? Array.Empty<byte>()).ToImmutableArray();
        }

        return new R2RTypeLayoutFixupPayload(type, size, alignment, layoutFlags, hfaType, gcLayout);
    }
}
