// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;

namespace ILCompiler.Reflection.ReadyToRun;

/// <summary>
/// Discriminator for a <see cref="SignaturePart"/>. Each value corresponds to one raw
/// wire-level field read from an R2R signature blob. The stream is grammar-aware: the
/// consumer follows the same grammar as the decoder to know which part to expect next.
///
/// Consumers are responsible for tracking their own module-context state by watching
/// <see cref="ModuleZapSigIndex"/>, <see cref="MethodModuleOverride"/>, and
/// <see cref="FixupModuleOverride"/> parts.
/// </summary>
public enum SignaturePartKind
{
    // ── Method signature ──────────────────────────────────────────────────
    /// <summary>Raw method-sig flags (compressed uint).</summary>
    MethodFlags,
    /// <summary>Module index from UpdateContext flag (compressed uint). Present only when
    /// <see cref="MethodFlags"/> has READYTORUN_METHOD_SIG_UpdateContext set.</summary>
    MethodModuleOverride,
    /// <summary>Method token RID (compressed uint).</summary>
    MethodRid,
    /// <summary>Generic method-instantiation argument count (compressed uint).</summary>
    MethodTypeArgCount,

    // ── Type signature ────────────────────────────────────────────────────
    /// <summary>Raw CorElementType byte (high bit already masked to 0).</summary>
    ElementType,
    /// <summary>Sentinel marker (<c>ELEMENT_TYPE_SENTINEL</c>) encountered while reading
    /// function-pointer parameters. No numeric value; the sentinel's position in the
    /// param sequence conveys the required parameter count.</summary>
    FnPtrSentinel,
    /// <summary>Raw encoded type token: <c>(rid &lt;&lt; 2) | tokenTable</c> (compressed uint).</summary>
    TypeToken,
    /// <summary>VAR / MVAR generic parameter index (compressed uint).</summary>
    GenericParamIndex,
    /// <summary>Multi-dimensional array rank (compressed uint).</summary>
    ArrayRank,
    /// <summary>Count of size dimensions (compressed uint).</summary>
    ArraySizeCount,
    /// <summary>One array dimension size (compressed uint).</summary>
    ArraySize,
    /// <summary>Count of lower-bound entries (compressed uint).</summary>
    ArrayLowerBoundCount,
    /// <summary>One array lower bound (compressed signed int).</summary>
    ArrayLowerBound,
    /// <summary>Generic instantiation argument count (compressed uint).</summary>
    GenericInstArgCount,
    /// <summary>Function-pointer signature header byte.</summary>
    FnPtrSigHeader,
    /// <summary>Function-pointer generic parameter count (compressed uint). Present only
    /// when <see cref="FnPtrSigHeader"/> indicates a generic signature.</summary>
    FnPtrGenericParamCount,
    /// <summary>Function-pointer parameter count (compressed uint).</summary>
    FnPtrParamCount,
    /// <summary>Module index from <c>ELEMENT_TYPE_MODULE_ZAPSIG</c> (compressed uint).</summary>
    ModuleZapSigIndex,

    // ── Field signature ───────────────────────────────────────────────────
    /// <summary>Raw field-sig flags (compressed uint).</summary>
    FieldFlags,
    /// <summary>Field token RID (compressed uint).</summary>
    FieldRid,

    // ── Fixup signature ───────────────────────────────────────────────────
    /// <summary>Raw fixup kind byte, including the <c>ModuleOverride</c> bit.
    /// Consumers mask with <c>~(byte)ReadyToRunFixupKind.ModuleOverride</c> to get the kind.</summary>
    FixupKindByte,
    /// <summary>Module index from fixup ModuleOverride prefix (compressed uint).</summary>
    FixupModuleOverride,
    /// <summary>Helper id (compressed uint).</summary>
    HelperId,
    /// <summary>User-string handle RID (compressed uint).</summary>
    UserStringToken,
    /// <summary>V-table slot index (compressed uint).</summary>
    VirtualSlotIndex,
    /// <summary>Virtual-function-override flags (compressed uint).</summary>
    VirtualOverrideFlags,
    /// <summary>Expected field offset for Check/Verify_FieldOffset (compressed uint).</summary>
    FieldExpectedOffset,
    /// <summary>Second value consumed by Verify_FieldOffset (compressed uint, currently unused by decoder).</summary>
    VerifyFieldOffsetSecond,
    /// <summary>Number of entries in an instruction-set support list (compressed uint).</summary>
    InstructionSetCount,
    /// <summary>One encoded instruction-set entry: <c>(setId &lt;&lt; 1) | supportedBit</c> (compressed uint).</summary>
    InstructionSetEncoded,
    /// <summary>Length of the IL byte blob for Check/Verify_IL_Body (compressed uint). The next
    /// part is <see cref="ILBodyBlob"/>.</summary>
    ILBodyByteCount,
    /// <summary>Raw IL bytes as a single blob; <see cref="SignaturePart.Blob"/> holds the bytes.</summary>
    ILBodyBlob,
    /// <summary>Count of token types following Check/Verify_IL_Body IL blob (compressed uint).</summary>
    ILBodyTypeCount,
    /// <summary>4-byte little-endian RVA for ResumptionStubEntryPoint fixup.</summary>
    ResumptionStubRva,

    // ── Type layout fixup payload ────────────────────────────────────────
    /// <summary>Type-layout flags (compressed uint).</summary>
    TypeLayoutFlags,
    /// <summary>Type size (compressed uint).</summary>
    TypeLayoutSize,
    /// <summary>HFA type byte. Present only when <c>READYTORUN_LAYOUT_HFA</c> is set.</summary>
    TypeLayoutHfaType,
    /// <summary>Alignment (compressed uint). Present only when <c>READYTORUN_LAYOUT_Alignment</c>
    /// is set and <c>READYTORUN_LAYOUT_Alignment_Native</c> is not.</summary>
    TypeLayoutAlignment,
    /// <summary>GC ref map blob. <see cref="SignaturePart.Blob"/> holds the bytes. The byte
    /// count is computed from the type size and target pointer size by the decoder.</summary>
    TypeLayoutGcRefBlob,
}

/// <summary>
/// One raw wire-level field from an R2R signature blob.
/// The <see cref="Kind"/> discriminates; <see cref="Value"/> holds a byte / uint / int / int32;
/// <see cref="Blob"/> is non-null only for blob-carrying kinds
/// (<see cref="SignaturePartKind.ILBodyBlob"/>, <see cref="SignaturePartKind.TypeLayoutGcRefBlob"/>).
/// </summary>
public readonly struct SignaturePart
{
    public SignaturePartKind Kind { get; }

    /// <summary>
    /// Scalar value for the part. Holds byte, uint, int, or int32 depending on Kind.
    /// For unsigned kinds the bits fit in a long without sign extension; for signed kinds
    /// (e.g. <see cref="SignaturePartKind.ArrayLowerBound"/>) the long is sign-extended.
    /// </summary>
    public long Value { get; }

    /// <summary>
    /// Byte blob. Non-null only for <see cref="SignaturePartKind.ILBodyBlob"/> and
    /// <see cref="SignaturePartKind.TypeLayoutGcRefBlob"/>.
    /// </summary>
    public byte[] Blob { get; }

    public SignaturePart(SignaturePartKind kind, long value)
    {
        Kind = kind;
        Value = value;
        Blob = null;
    }

    public SignaturePart(SignaturePartKind kind, byte[] blob)
    {
        Kind = kind;
        Value = blob is null ? 0 : blob.Length;
        Blob = blob;
    }

    public override string ToString()
        => Blob is null ? $"{Kind}=0x{Value:X}" : $"{Kind}={Blob.Length}bytes";
}

/// <summary>
/// A raw R2R signature materialized as a flat stream of <see cref="SignaturePart"/>
/// values plus the start/end byte offsets in the underlying image.
/// </summary>
public sealed class R2RSignature
{
    public ImmutableArray<SignaturePart> Parts { get; }

    /// <summary>Byte offset in the underlying NativeReader where the signature started.</summary>
    public int StartOffset { get; }

    /// <summary>Byte offset in the underlying NativeReader immediately after the signature.</summary>
    public int EndOffset { get; }

    public R2RSignature(ImmutableArray<SignaturePart> parts, int startOffset, int endOffset)
    {
        Parts = parts;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public int ByteLength => EndOffset - StartOffset;

    public IEnumerable<SignaturePart> AsEnumerable() => Parts;
}
