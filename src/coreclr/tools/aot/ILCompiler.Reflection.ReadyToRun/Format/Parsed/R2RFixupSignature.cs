// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text;
using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Format.Parsed;

/// <summary>
/// Structural representation of a decoded fixup signature in an R2R image.
/// The payload is a discriminated union keyed by the fixup kind.
/// </summary>
public sealed class R2RFixupSignature
{
    /// <summary>
    /// The fixup kind (e.g., TypeHandle, MethodEntry, FieldAddress).
    /// </summary>
    public ReadyToRunFixupKind Kind { get; }

    /// <summary>
    /// Module index from ModuleOverride prefix, or -1 if no module override.
    /// </summary>
    public int ModuleIndex { get; }

    /// <summary>
    /// The decoded payload, specific to the fixup kind.
    /// </summary>
    public R2RFixupPayload Payload { get; }

    public R2RFixupSignature(ReadyToRunFixupKind kind, int moduleIndex, R2RFixupPayload payload)
    {
        Kind = kind;
        ModuleIndex = moduleIndex;
        Payload = payload;
    }

    public void AppendTo(StringBuilder sb)
    {
        sb.Append(Kind.ToString());
        if (ModuleIndex >= 0)
            sb.Append($" [module:{ModuleIndex}]");
        sb.Append(' ');
        Payload.AppendTo(sb);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }
}

/// <summary>
/// Base class for fixup signature payloads. Each subclass represents a different
/// payload shape corresponding to one or more fixup kinds.
/// </summary>
public abstract class R2RFixupPayload
{
    public abstract void AppendTo(StringBuilder sb);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }
}

/// <summary>
/// Payload containing a single type reference.
/// Used by: TypeHandle, NewObject, NewArray, IsInstanceOf, ChkCast, CctorTrigger,
/// StaticBaseNonGC, StaticBaseGC, ThreadStaticBaseNonGC, ThreadStaticBaseGC,
/// FieldBaseOffset, TypeDictionary, DeclaringTypeHandle.
/// </summary>
public sealed class R2RTypeFixupPayload : R2RFixupPayload
{
    public R2RTypeNode Type { get; }

    public R2RTypeFixupPayload(R2RTypeNode type) => Type = type;

    public override void AppendTo(StringBuilder sb) => Type.AppendTo(sb);
}

/// <summary>
/// Payload containing a method reference.
/// Used by: MethodHandle, MethodEntry, VirtualEntry, MethodDictionary,
/// IndirectPInvokeTarget, PInvokeTarget.
/// </summary>
public sealed class R2RMethodFixupPayload : R2RFixupPayload
{
    public R2RMethodRef Method { get; }

    public R2RMethodFixupPayload(R2RMethodRef method) => Method = method;

    public override void AppendTo(StringBuilder sb) => Method.AppendTo(sb);
}

/// <summary>
/// Payload containing a field reference.
/// Used by: FieldHandle, FieldAddress.
/// </summary>
public sealed class R2RFieldFixupPayload : R2RFixupPayload
{
    public R2RFieldRef Field { get; }

    public R2RFieldFixupPayload(R2RFieldRef field) => Field = field;

    public override void AppendTo(StringBuilder sb) => Field.AppendTo(sb);
}

/// <summary>
/// Payload containing a raw method token (def or ref) without full method signature decoding.
/// Used by: MethodEntry_DefToken, MethodEntry_RefToken, VirtualEntry_DefToken, VirtualEntry_RefToken.
/// </summary>
public sealed class R2RTokenFixupPayload : R2RFixupPayload
{
    public bool IsMemberRef { get; }
    public int Rid { get; }

    public R2RTokenFixupPayload(bool isMemberRef, int rid)
    {
        IsMemberRef = isMemberRef;
        Rid = rid;
    }

    public override void AppendTo(StringBuilder sb)
    {
        string kindName = IsMemberRef ? "MemberRef" : "MethodDef";
        sb.Append($"{kindName}:{Rid:X6}");
    }
}

/// <summary>
/// Payload containing a helper ID.
/// Used by: Helper.
/// </summary>
public sealed class R2RHelperFixupPayload : R2RFixupPayload
{
    public ReadyToRunHelper HelperId { get; }

    public R2RHelperFixupPayload(ReadyToRunHelper helperId) => HelperId = helperId;

    public override void AppendTo(StringBuilder sb) => sb.Append(HelperId.ToString());
}

/// <summary>
/// Payload containing a string handle RID.
/// Used by: StringHandle.
/// </summary>
public sealed class R2RStringFixupPayload : R2RFixupPayload
{
    public int Rid { get; }

    public R2RStringFixupPayload(int rid) => Rid = rid;

    public override void AppendTo(StringBuilder sb) => sb.Append($"UserString:{Rid:X6}");
}

/// <summary>
/// Payload for dictionary lookup fixups.
/// Used by: ThisObjDictionaryLookup (has LookupType), TypeDictionaryLookup, MethodDictionaryLookup.
/// </summary>
public sealed class R2RDictionaryLookupFixupPayload : R2RFixupPayload
{
    /// <summary>
    /// For ThisObjDictionaryLookup, the type context for the lookup. Null for Type/MethodDictionaryLookup.
    /// </summary>
    public R2RTypeNode LookupType { get; }

    /// <summary>
    /// The inner fixup signature describing what is being looked up.
    /// </summary>
    public R2RFixupSignature InnerSignature { get; }

    public R2RDictionaryLookupFixupPayload(R2RTypeNode lookupType, R2RFixupSignature innerSignature)
    {
        LookupType = lookupType;
        InnerSignature = innerSignature;
    }

    public override void AppendTo(StringBuilder sb)
    {
        if (LookupType != null)
        {
            sb.Append("context:");
            LookupType.AppendTo(sb);
            sb.Append(' ');
        }
        sb.Append("lookup:");
        InnerSignature.AppendTo(sb);
    }
}

/// <summary>
/// Payload for type layout checks/verifications.
/// Used by: Check_TypeLayout, Verify_TypeLayout, ContinuationLayout.
/// </summary>
public sealed class R2RTypeLayoutFixupPayload : R2RFixupPayload
{
    public R2RTypeNode Type { get; }
    public uint Size { get; }
    public uint Alignment { get; }
    public ReadyToRunTypeLayoutFlags LayoutFlags { get; }
    public byte HfaType { get; }
    public ImmutableArray<byte> GcLayout { get; }

    public R2RTypeLayoutFixupPayload(
        R2RTypeNode type,
        uint size,
        uint alignment,
        ReadyToRunTypeLayoutFlags layoutFlags,
        byte hfaType,
        ImmutableArray<byte> gcLayout)
    {
        Type = type;
        Size = size;
        Alignment = alignment;
        LayoutFlags = layoutFlags;
        HfaType = hfaType;
        GcLayout = gcLayout;
    }

    public override void AppendTo(StringBuilder sb)
    {
        Type.AppendTo(sb);
        sb.Append($" size:0x{Size:X} align:0x{Alignment:X}");
        if ((LayoutFlags & ReadyToRunTypeLayoutFlags.READYTORUN_LAYOUT_HFA) != 0)
            sb.Append($" hfa:{HfaType:X2}");
        if (!GcLayout.IsEmpty)
            sb.Append($" gc:{GcLayout.Length}bytes");
    }
}

/// <summary>
/// Payload for field offset checks/verifications.
/// Used by: Check_FieldOffset, Verify_FieldOffset.
/// </summary>
public sealed class R2RFieldOffsetFixupPayload : R2RFixupPayload
{
    public uint ExpectedOffset { get; }
    public R2RFieldRef Field { get; }

    public R2RFieldOffsetFixupPayload(uint expectedOffset, R2RFieldRef field)
    {
        ExpectedOffset = expectedOffset;
        Field = field;
    }

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append($"offset:0x{ExpectedOffset:X} ");
        Field.AppendTo(sb);
    }
}

/// <summary>
/// Payload for virtual function override checks/verifications.
/// Used by: Check_VirtualFunctionOverride, Verify_VirtualFunctionOverride.
/// </summary>
public sealed class R2RVirtualOverrideFixupPayload : R2RFixupPayload
{
    public uint OverrideFlags { get; }
    public R2RMethodRef Declaration { get; }
    public R2RTypeNode ImplementationType { get; }
    public R2RTypeNode DeclaringTypeHandle { get; }
    public R2RMethodRef ImplementationMethod { get; }

    public R2RVirtualOverrideFixupPayload(
        uint overrideFlags,
        R2RMethodRef declaration,
        R2RTypeNode implementationType,
        R2RTypeNode declaringTypeHandle,
        R2RMethodRef implementationMethod)
    {
        OverrideFlags = overrideFlags;
        Declaration = declaration;
        ImplementationType = implementationType;
        DeclaringTypeHandle = declaringTypeHandle;
        ImplementationMethod = implementationMethod;
    }

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append("decl:");
        Declaration.AppendTo(sb);
        sb.Append(" implType:");
        ImplementationType.AppendTo(sb);
        sb.Append(" impl:");
        ImplementationMethod.AppendTo(sb);
    }
}

/// <summary>
/// Payload for instruction set support checks.
/// Used by: Check_InstructionSetSupport.
/// </summary>
public sealed class R2RInstructionSetFixupPayload : R2RFixupPayload
{
    public ImmutableArray<ushort> SupportedSets { get; }
    public ImmutableArray<ushort> UnsupportedSets { get; }

    public R2RInstructionSetFixupPayload(ImmutableArray<ushort> supportedSets, ImmutableArray<ushort> unsupportedSets)
    {
        SupportedSets = supportedSets;
        UnsupportedSets = unsupportedSets;
    }

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append("supported:[");
        for (int i = 0; i < SupportedSets.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"0x{SupportedSets[i]:X}");
        }
        sb.Append("] unsupported:[");
        for (int i = 0; i < UnsupportedSets.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"0x{UnsupportedSets[i]:X}");
        }
        sb.Append(']');
    }
}

/// <summary>
/// Payload for IL body checks/verifications.
/// Used by: Check_IL_Body, Verify_IL_Body.
/// </summary>
public sealed class R2RILBodyFixupPayload : R2RFixupPayload
{
    public R2RMethodRef Method { get; }
    public ImmutableArray<byte> ILBytes { get; }
    public ImmutableArray<R2RTypeNode> TokenTypes { get; }

    public R2RILBodyFixupPayload(R2RMethodRef method, ImmutableArray<byte> ilBytes, ImmutableArray<R2RTypeNode> tokenTypes)
    {
        Method = method;
        ILBytes = ilBytes;
        TokenTypes = tokenTypes;
    }

    public override void AppendTo(StringBuilder sb)
    {
        Method.AppendTo(sb);
        sb.Append($" il:{ILBytes.Length}bytes types:{TokenTypes.Length}");
    }
}

/// <summary>
/// Payload for delegate constructor optimization.
/// Used by: DelegateCtor.
/// </summary>
public sealed class R2RDelegateCtorFixupPayload : R2RFixupPayload
{
    public R2RMethodRef TargetMethod { get; }
    public R2RTypeNode DelegateType { get; }

    public R2RDelegateCtorFixupPayload(R2RMethodRef targetMethod, R2RTypeNode delegateType)
    {
        TargetMethod = targetMethod;
        DelegateType = delegateType;
    }

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append("target:");
        TargetMethod.AppendTo(sb);
        sb.Append(" delegate:");
        DelegateType.AppendTo(sb);
    }
}

/// <summary>
/// Payload for slot-based virtual entry.
/// Used by: VirtualEntry_Slot (obsolete).
/// </summary>
public sealed class R2RSlotFixupPayload : R2RFixupPayload
{
    public int Slot { get; }
    public R2RTypeNode Type { get; }

    public R2RSlotFixupPayload(int slot, R2RTypeNode type)
    {
        Slot = slot;
        Type = type;
    }

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append($"slot:{Slot} ");
        Type.AppendTo(sb);
    }
}

/// <summary>
/// Payload for a resumption stub entry point RVA.
/// Used by: ResumptionStubEntryPoint.
/// </summary>
public sealed class R2RStubEntryPointFixupPayload : R2RFixupPayload
{
    public int Rva { get; }

    public R2RStubEntryPointFixupPayload(int rva) => Rva = rva;

    public override void AppendTo(StringBuilder sb) => sb.Append($"RVA:0x{Rva:X8}");
}

/// <summary>
/// Payload for field offset fixup (non-check variant).
/// Used by: FieldOffset.
/// </summary>
public sealed class R2RFieldOffsetValueFixupPayload : R2RFixupPayload
{
    public R2RFieldRef Field { get; }

    public R2RFieldOffsetValueFixupPayload(R2RFieldRef field) => Field = field;

    public override void AppendTo(StringBuilder sb) => Field.AppendTo(sb);
}

/// <summary>
/// Empty payload for fixup kinds that have no additional data, or for unrecognized kinds.
/// </summary>
public sealed class R2REmptyFixupPayload : R2RFixupPayload
{
    public static R2REmptyFixupPayload Instance { get; } = new();

    private R2REmptyFixupPayload() { }

    public override void AppendTo(StringBuilder sb) { }
}
