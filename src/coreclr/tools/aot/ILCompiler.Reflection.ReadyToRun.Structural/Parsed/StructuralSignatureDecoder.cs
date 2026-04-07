// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using Internal.ReadyToRunConstants;

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural.Parsed;

/// <summary>
/// A signature decoder that produces structural AST nodes (<see cref="R2RTypeNode"/>,
/// <see cref="R2RMethodRef"/>) rather than formatted strings. This enables fully-materialized
/// fixup signature parsing where results contain no image back-pointers.
///
/// <para>
/// Extends <see cref="R2RSignatureDecoder{TType, TMethod, TGenericContext}"/> with
/// <see cref="R2RStructuralTypeProvider"/> so that <see cref="R2RSignatureDecoder{TType,TMethod,TGenericContext}.ParseType"/>
/// returns <see cref="R2RTypeNode"/> and <see cref="R2RSignatureDecoder{TType,TMethod,TGenericContext}.ParseMethod"/>
/// returns <see cref="R2RMethodRef"/>.
/// </para>
/// </summary>
internal sealed class StructuralSignatureDecoder
    : R2RSignatureDecoder<R2RTypeNode, R2RMethodRef, R2RStructuralContext>
{
    public StructuralSignatureDecoder(
        R2RStructuralContext context,
        MetadataReader metadataReader,
        ILCompiler.Reflection.ReadyToRun.ReadyToRunReader legacyReader,
        int offset)
        : base(R2RStructuralTypeProvider.Instance, context, metadataReader, legacyReader, offset, skipOverrideMetadataReader: false)
    {
    }

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

        R2RFixupPayload payload = ParseFixupPayload(fixupKind);
        return new R2RFixupSignature(fixupKind, moduleIndex, payload);
    }

    /// <summary>
    /// Parse the fixup payload based on the fixup kind. The decoder is positioned
    /// immediately after the fixup kind byte and optional module override.
    /// </summary>
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
            {
                int rid = (int)ReadUInt();
                return new R2RTokenFixupPayload(isMemberRef: false, rid);
            }

            case ReadyToRunFixupKind.MethodEntry_RefToken:
            case ReadyToRunFixupKind.VirtualEntry_RefToken:
            {
                int rid = (int)ReadUInt();
                return new R2RTokenFixupPayload(isMemberRef: true, rid);
            }

            // Slot-based virtual entry
            case ReadyToRunFixupKind.VirtualEntry_Slot:
            {
                int slot = (int)ReadUInt();
                R2RTypeNode type = ParseType();
                return new R2RSlotFixupPayload(slot, type);
            }

            // Helper
            case ReadyToRunFixupKind.Helper:
            {
                uint helperId = ReadUInt();
                return new R2RHelperFixupPayload((ReadyToRunHelper)helperId);
            }

            // String handle
            case ReadyToRunFixupKind.StringHandle:
            {
                int rid = (int)ReadUInt();
                return new R2RStringFixupPayload(rid);
            }

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
                uint stubRva = BitConverter.ToUInt32(_image, Offset);
                SkipBytes(4);
                return new R2RStubEntryPointFixupPayload((int)stubRva);
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
                ReadUInt(); // second value (unknown purpose in text decoder)
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
                    bool isSupported = (encoded & 1) == 1;
                    if (isSupported)
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
                return new R2RILBodyFixupPayload(method, ilBytes.ToImmutableArray(), types.ToImmutable());
            }

            default:
                return R2REmptyFixupPayload.Instance;
        }
    }

    /// <summary>
    /// Parse a field reference from the signature stream.
    /// Reads field sig flags, optional owner type, and the field token.
    /// </summary>
    private R2RFieldRef ParseFieldRef()
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

    /// <summary>
    /// Parse the type layout data following a type signature for Check_TypeLayout/Verify_TypeLayout/ContinuationLayout fixups.
    /// </summary>
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
                int pointerSize = _contextReader.TargetPointerSize;
                int cbGCRefMap = ((int)size / pointerSize + 7) / 8;
                var builder = ImmutableArray.CreateBuilder<byte>(cbGCRefMap);
                for (int i = 0; i < cbGCRefMap; i++)
                    builder.Add(ReadByte());
                gcLayout = builder.ToImmutable();
            }
        }

        return new R2RTypeLayoutFixupPayload(type, size, alignment, layoutFlags, hfaType, gcLayout);
    }
}
