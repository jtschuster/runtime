// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;

using Internal.CorConstants;
using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Structural;

/// <summary>
/// A pure-structural R2R signature decoder that parses signature bytes into AST nodes
/// (<see cref="R2RTypeNode"/>, <see cref="R2RMethodRef"/>, <see cref="R2RFixupSignature"/>)
/// without requiring <see cref="MetadataReader"/> or assembly resolution.
///
/// <para>
/// When the decoder encounters module-switching constructs (ModuleOverride, UpdateContext,
/// MODULE_ZAPSIG), it records the raw module index on the AST node. Token references
/// (TypeDef, TypeRef, TypeSpec, MethodDef, MemberRef) are stored as (HandleKind, RID) pairs.
/// The resolution of these tokens to names and types is deferred to a higher-level layer
/// that has access to MetadataReaders.
/// </para>
/// </summary>
public sealed class RawSignatureDecoder
{
    private readonly NativeReader _reader;
    private int _offset;
    private readonly int _targetPointerSize;

    /// <summary>
    /// The current module context. Starts at -1 (meaning the component's own module).
    /// Updated by ModuleOverride prefix and UpdateContext flag.
    /// </summary>
    private int _currentModuleIndex;

    public int Offset => _offset;

    public RawSignatureDecoder(NativeReader reader, int offset, int targetPointerSize, int initialModuleIndex = -1)
    {
        _reader = reader;
        _offset = offset;
        _targetPointerSize = targetPointerSize;
        _currentModuleIndex = initialModuleIndex;
    }

    // ── Byte-reading primitives (delegate to NativeReader) ───────────────

    private byte ReadByte() => _reader.ReadByte(ref _offset);

    private void SkipBytes(int count)
    {
        checked { _offset += count; }
    }

    /// <summary>
    /// Read a compressed unsigned 32-bit integer (CorSigUncompressData format).
    /// </summary>
    private uint ReadUInt() => _reader.ReadCompressedData(ref _offset);

    /// <summary>
    /// Read a compressed signed integer.
    /// </summary>
    private int ReadInt()
    {
        uint raw = ReadUInt();
        int data = (int)(raw >> 1);
        return (raw & 1) == 0 ? data : -data;
    }

    /// <summary>
    /// Read an encoded type token. The low 2 bits encode the token table
    /// (TypeDef=0, TypeRef=1, TypeSpec=2, BaseType=3) and the remaining bits are the RID.
    /// </summary>
    private (HandleKind kind, int rid) ReadTypeToken()
    {
        uint encoded = ReadUInt();
        int rid = (int)(encoded >> 2);
        HandleKind kind = (encoded & 3) switch
        {
            0 => HandleKind.TypeDefinition,
            1 => HandleKind.TypeReference,
            2 => HandleKind.TypeSpecification,
            3 => HandleKind.TypeDefinition, // BaseType maps to TypeDef row 0
            _ => throw new BadImageFormatException()
        };
        return (kind, rid);
    }

    private CorElementType ReadElementType() => (CorElementType)(ReadByte() & 0x7F);

    private CorElementType PeekElementType()
    {
        int peekOffset = _offset;
        return (CorElementType)(_reader.ReadByte(ref peekOffset) & 0x7F);
    }

    // ── Type parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Parse a type signature from the current position.
    /// </summary>
    public R2RTypeNode ParseType()
    {
        CorElementType elemType = ReadElementType();
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
                return new R2RPointerTypeNode(ParseType());

            case CorElementType.ELEMENT_TYPE_BYREF:
                return new R2RByRefTypeNode(ParseType());

            case CorElementType.ELEMENT_TYPE_VALUETYPE:
            case CorElementType.ELEMENT_TYPE_CLASS:
            {
                bool isValueType = elemType == CorElementType.ELEMENT_TYPE_VALUETYPE;
                var (kind, rid) = ReadTypeToken();
                return new R2RTokenTypeNode(_currentModuleIndex, kind, rid, isValueType);
            }

            case CorElementType.ELEMENT_TYPE_VAR:
                return new R2RGenericTypeParamNode((int)ReadUInt());

            case CorElementType.ELEMENT_TYPE_MVAR:
                return new R2RGenericMethodParamNode((int)ReadUInt());

            case CorElementType.ELEMENT_TYPE_ARRAY:
            {
                R2RTypeNode elementType = ParseType();
                uint rank = ReadUInt();
                if (rank == 0)
                    return new R2RSzArrayTypeNode(elementType);

                uint sizeCount = ReadUInt();
                int[] sizes = new int[sizeCount];
                for (uint i = 0; i < sizeCount; i++)
                    sizes[i] = (int)ReadUInt();

                uint lowerBoundCount = ReadUInt();
                int[] lowerBounds = new int[lowerBoundCount];
                for (uint i = 0; i < lowerBoundCount; i++)
                    lowerBounds[i] = ReadInt();

                var shape = new ArrayShape((int)rank, sizes.ToImmutableArray(), lowerBounds.ToImmutableArray());
                return new R2RArrayTypeNode(elementType, shape);
            }

            case CorElementType.ELEMENT_TYPE_SZARRAY:
                return new R2RSzArrayTypeNode(ParseType());

            case CorElementType.ELEMENT_TYPE_GENERICINST:
            {
                R2RTypeNode genericType = ParseType();
                uint argCount = ReadUInt();
                var args = ImmutableArray.CreateBuilder<R2RTypeNode>((int)argCount);
                for (uint i = 0; i < argCount; i++)
                    args.Add(ParseType());
                return new R2RGenericInstTypeNode(genericType, args.MoveToImmutable());
            }

            case CorElementType.ELEMENT_TYPE_FNPTR:
            {
                var sigHeader = new SignatureHeader(ReadByte());
                int genericParamCount = sigHeader.IsGeneric ? (int)ReadUInt() : 0;
                int paramCount = (int)ReadUInt();
                R2RTypeNode returnType = ParseType();
                var paramTypes = ImmutableArray.CreateBuilder<R2RTypeNode>(paramCount);
                int requiredParamCount = -1;
                for (int i = 0; i < paramCount; i++)
                {
                    while (PeekElementType() == CorElementType.ELEMENT_TYPE_SENTINEL)
                    {
                        requiredParamCount = i;
                        ReadElementType();
                    }
                    paramTypes.Add(ParseType());
                }
                if (requiredParamCount == -1)
                    requiredParamCount = paramCount;

                var methodSig = new MethodSignature<R2RTypeNode>(sigHeader, returnType, requiredParamCount, genericParamCount, paramTypes.MoveToImmutable());
                return new R2RFunctionPointerTypeNode(methodSig);
            }

            case CorElementType.ELEMENT_TYPE_CMOD_REQD:
            {
                var (kind, rid) = ReadTypeToken();
                R2RTypeNode modifier = new R2RTokenTypeNode(_currentModuleIndex, kind, rid, false);
                return new R2RModifiedTypeNode(modifier, ParseType(), isRequired: true);
            }

            case CorElementType.ELEMENT_TYPE_CMOD_OPT:
            {
                var (kind, rid) = ReadTypeToken();
                R2RTypeNode modifier = new R2RTokenTypeNode(_currentModuleIndex, kind, rid, false);
                return new R2RModifiedTypeNode(modifier, ParseType(), isRequired: false);
            }

            case CorElementType.ELEMENT_TYPE_PINNED:
                return new R2RPinnedTypeNode(ParseType());

            case CorElementType.ELEMENT_TYPE_CANON_ZAPSIG:
                return R2RCanonTypeNode.Instance;

            case CorElementType.ELEMENT_TYPE_MODULE_ZAPSIG:
            {
                int moduleIndex = (int)ReadUInt();
                int savedModule = _currentModuleIndex;
                _currentModuleIndex = moduleIndex;
                R2RTypeNode innerType = ParseType();
                _currentModuleIndex = savedModule;
                return new R2RModuleQualifiedTypeNode(moduleIndex, innerType);
            }

            case CorElementType.ELEMENT_TYPE_VAR_ZAPSIG:
                throw new BadImageFormatException("ELEMENT_TYPE_VAR_ZAPSIG not supported in structural decoder");

            case CorElementType.ELEMENT_TYPE_NATIVE_VALUETYPE_ZAPSIG:
                throw new BadImageFormatException("ELEMENT_TYPE_NATIVE_VALUETYPE_ZAPSIG not supported in structural decoder");

            default:
                throw new BadImageFormatException($"Unexpected element type: 0x{(byte)elemType:X2}");
        }
    }

    // ── Method parsing ───────────────────────────────────────────────────

    /// <summary>
    /// Parse a method reference from the current position.
    /// </summary>
    public R2RMethodRef ParseMethod()
    {
        uint methodFlags = ReadUInt();

        int moduleIndex = _currentModuleIndex;
        if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UpdateContext) != 0)
        {
            moduleIndex = (int)ReadUInt();
        }

        // Save/restore module context for tokens within this method
        int savedModule = _currentModuleIndex;
        _currentModuleIndex = moduleIndex;

        R2RTypeNode ownerType = null;
        if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_OwnerType) != 0)
        {
            ownerType = ParseType();
        }

        bool isMemberRef = (methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MemberRefToken) != 0;

        if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_SlotInsteadOfToken) != 0)
        {
            throw new NotImplementedException("SlotInsteadOfToken");
        }

        int rid = (int)ReadUInt();

        ImmutableArray<R2RTypeNode> typeArgs = ImmutableArray<R2RTypeNode>.Empty;
        if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_MethodInstantiation) != 0)
        {
            uint argCount = ReadUInt();
            var args = ImmutableArray.CreateBuilder<R2RTypeNode>((int)argCount);
            for (uint i = 0; i < argCount; i++)
                args.Add(ParseType());
            typeArgs = args.MoveToImmutable();
        }

        R2RTypeNode constrainedType = null;
        if ((methodFlags & (uint)ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_Constrained) != 0)
        {
            constrainedType = ParseType();
        }

        _currentModuleIndex = savedModule;

        // Extract the "prefix" flags that go on the method ref (Unboxing, Instantiating, Async)
        var prefixFlags = (ReadyToRunMethodSigFlags)(methodFlags & (uint)(
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub |
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub |
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant));

        return new R2RMethodRef(prefixFlags, moduleIndex, ownerType, isMemberRef, rid, typeArgs, constrainedType);
    }

    // ── Field parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Parse a field reference from the current position.
    /// </summary>
    public R2RFieldRef ParseFieldRef()
    {
        uint flags = ReadUInt();
        R2RTypeNode ownerType = null;
        if ((flags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_OwnerType) != 0)
        {
            ownerType = ParseType();
        }

        bool isMemberRef = (flags & (uint)ReadyToRunFieldSigFlags.READYTORUN_FIELD_SIG_MemberRefToken) != 0;
        int rid = (int)ReadUInt();

        return new R2RFieldRef((ReadyToRunFieldSigFlags)flags, ownerType, isMemberRef, rid);
    }

    // ── Fixup signature parsing ──────────────────────────────────────────

    /// <summary>
    /// Parse a complete fixup signature from the current position.
    /// Reads the fixup kind byte, optional module override, and the kind-specific payload.
    /// </summary>
    public R2RFixupSignature ParseFixupSignature()
    {
        byte fixupByte = ReadByte();
        bool moduleOverride = (fixupByte & (byte)ReadyToRunFixupKind.ModuleOverride) != 0;
        var fixupKind = (ReadyToRunFixupKind)(fixupByte & ~(byte)ReadyToRunFixupKind.ModuleOverride);

        int moduleIndex = -1;
        if (moduleOverride)
        {
            moduleIndex = (int)ReadUInt();
        }

        // Set module context for the payload
        int savedModule = _currentModuleIndex;
        if (moduleIndex >= 0)
            _currentModuleIndex = moduleIndex;

        R2RFixupPayload payload = ParseFixupPayload(fixupKind);

        _currentModuleIndex = savedModule;
        return new R2RFixupSignature(fixupKind, moduleIndex, payload);
    }

    private R2RFixupPayload ParseFixupPayload(ReadyToRunFixupKind fixupKind)
    {
        switch (fixupKind)
        {
            // Dictionary lookups: optional context type + recursive inner signature
            case ReadyToRunFixupKind.ThisObjDictionaryLookup:
            {
                R2RTypeNode lookupType = ParseType();
                R2RFixupSignature inner = ParseFixupSignature();
                return new R2RDictionaryLookupFixupPayload(lookupType, inner);
            }

            case ReadyToRunFixupKind.TypeDictionaryLookup:
            case ReadyToRunFixupKind.MethodDictionaryLookup:
            {
                R2RFixupSignature inner = ParseFixupSignature();
                return new R2RDictionaryLookupFixupPayload(null, inner);
            }

            // Type-based fixups
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
                return new R2RTypeFixupPayload(ParseType());

            // Method-based fixups
            case ReadyToRunFixupKind.MethodHandle:
            case ReadyToRunFixupKind.MethodEntry:
            case ReadyToRunFixupKind.VirtualEntry:
            case ReadyToRunFixupKind.MethodDictionary:
            case ReadyToRunFixupKind.IndirectPInvokeTarget:
            case ReadyToRunFixupKind.PInvokeTarget:
                return new R2RMethodFixupPayload(ParseMethod());

            // Field-based fixups
            case ReadyToRunFixupKind.FieldHandle:
            case ReadyToRunFixupKind.FieldAddress:
                return new R2RFieldFixupPayload(ParseFieldRef());

            case ReadyToRunFixupKind.FieldOffset:
                return new R2RFieldOffsetValueFixupPayload(ParseFieldRef());

            // Token-only fixups (MethodDef or MemberRef RID)
            case ReadyToRunFixupKind.MethodEntry_DefToken:
            case ReadyToRunFixupKind.VirtualEntry_DefToken:
                return new R2RTokenFixupPayload(isMemberRef: false, (int)ReadUInt());

            case ReadyToRunFixupKind.MethodEntry_RefToken:
            case ReadyToRunFixupKind.VirtualEntry_RefToken:
                return new R2RTokenFixupPayload(isMemberRef: true, (int)ReadUInt());

            // Slot-based virtual entry
            case ReadyToRunFixupKind.VirtualEntry_Slot:
            {
                int slot = (int)ReadUInt();
                R2RTypeNode type = ParseType();
                return new R2RSlotFixupPayload(slot, type);
            }

            // Helper
            case ReadyToRunFixupKind.Helper:
                return new R2RHelperFixupPayload((ReadyToRunHelper)ReadUInt());

            // String handle
            case ReadyToRunFixupKind.StringHandle:
                return new R2RStringFixupPayload((int)ReadUInt());

            // Type layout checks
            case ReadyToRunFixupKind.Check_TypeLayout:
            case ReadyToRunFixupKind.Verify_TypeLayout:
            case ReadyToRunFixupKind.ContinuationLayout:
            {
                R2RTypeNode type = ParseType();
                return ParseTypeLayoutPayload(type);
            }

            // Resumption stub entry point
            case ReadyToRunFixupKind.ResumptionStubEntryPoint:
            {
                int stubRva = _reader.ReadInt32(ref _offset);
                return new R2RStubEntryPointFixupPayload(stubRva);
            }

            // Virtual function override checks
            case ReadyToRunFixupKind.Check_VirtualFunctionOverride:
            case ReadyToRunFixupKind.Verify_VirtualFunctionOverride:
            {
                uint flags = ReadUInt();
                R2RMethodRef declaration = ParseMethod();
                R2RTypeNode implType = ParseType();

                R2RMethodRef implMethod = null;
                if (((ReadyToRunVirtualFunctionOverrideFlags)flags).HasFlag(
                    ReadyToRunVirtualFunctionOverrideFlags.VirtualFunctionOverridden))
                {
                    implMethod = ParseMethod();
                }

                return new R2RVirtualOverrideFixupPayload(flags, declaration, implType, declaringTypeHandle: null, implMethod);
            }

            // Field offset checks
            case ReadyToRunFixupKind.Check_FieldOffset:
            {
                uint expectedOffset = ReadUInt();
                R2RFieldRef field = ParseFieldRef();
                return new R2RFieldOffsetFixupPayload(expectedOffset, field);
            }

            case ReadyToRunFixupKind.Verify_FieldOffset:
            {
                uint expectedOffset = ReadUInt();
                ReadUInt(); // second value
                R2RFieldRef field = ParseFieldRef();
                return new R2RFieldOffsetFixupPayload(expectedOffset, field);
            }

            // Instruction set support check
            case ReadyToRunFixupKind.Check_InstructionSetSupport:
            {
                uint count = ReadUInt();
                var supported = ImmutableArray.CreateBuilder<ushort>();
                var unsupported = ImmutableArray.CreateBuilder<ushort>();
                for (uint i = 0; i < count; i++)
                {
                    uint encoded = ReadUInt();
                    ushort instructionSet = (ushort)(encoded >> 1);
                    if ((encoded & 1) == 1)
                        supported.Add(instructionSet);
                    else
                        unsupported.Add(instructionSet);
                }
                return new R2RInstructionSetFixupPayload(supported.ToImmutable(), unsupported.ToImmutable());
            }

            // Delegate constructor
            case ReadyToRunFixupKind.DelegateCtor:
            {
                R2RMethodRef targetMethod = ParseMethod();
                R2RTypeNode delegateType = ParseType();
                return new R2RDelegateCtorFixupPayload(targetMethod, delegateType);
            }

            // IL body checks
            case ReadyToRunFixupKind.Check_IL_Body:
            case ReadyToRunFixupKind.Verify_IL_Body:
            {
                uint ilByteCount = ReadUInt();
                byte[] ilBytes = new byte[ilByteCount];
                for (uint i = 0; i < ilByteCount; i++)
                    ilBytes[i] = ReadByte();

                uint typeCount = ReadUInt();
                var types = ImmutableArray.CreateBuilder<R2RTypeNode>((int)typeCount);
                for (uint i = 0; i < typeCount; i++)
                    types.Add(ParseType());

                R2RMethodRef method = ParseMethod();
                return new R2RILBodyFixupPayload(method, ilBytes.ToImmutableArray(), types.MoveToImmutable());
            }

            default:
                return R2REmptyFixupPayload.Instance;
        }
    }

    private R2RTypeLayoutFixupPayload ParseTypeLayoutPayload(R2RTypeNode type)
    {
        var layoutFlags = (ReadyToRunTypeLayoutFlags)ReadUInt();
        uint size = ReadUInt();

        byte hfaType = 0;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_HFA))
        {
            hfaType = (byte)ReadUInt();
        }

        uint alignment = 0;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment))
        {
            if (!layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_Alignment_Native))
            {
                alignment = ReadUInt();
            }
        }

        var gcLayout = ImmutableArray<byte>.Empty;
        if (layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout))
        {
            if (!layoutFlags.HasFlag(ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_GCLayout_Empty))
            {
                int cbGCRefMap = ((int)size / _targetPointerSize + 7) / 8;
                var builder = ImmutableArray.CreateBuilder<byte>(cbGCRefMap);
                for (int i = 0; i < cbGCRefMap; i++)
                    builder.Add(ReadByte());
                gcLayout = builder.MoveToImmutable();
            }
        }

        return new R2RTypeLayoutFixupPayload(type, size, alignment, layoutFlags, hfaType, gcLayout);
    }
}
