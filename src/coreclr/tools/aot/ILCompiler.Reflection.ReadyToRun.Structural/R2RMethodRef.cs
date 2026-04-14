// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;
using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun.Structural;

/// <summary>
/// Structural representation of a method reference in an R2R signature.
/// Stores the raw token and flags without resolving method names from metadata.
/// </summary>
public sealed class R2RMethodRef
{
    /// <summary>
    /// Flags from the method signature encoding.
    /// Only contains the "prefix" flags (UnboxingStub, InstantiatingStub, AsyncVariant).
    /// Structural flags (OwnerType, MemberRefToken, etc.) are reflected in the property values.
    /// </summary>
    public ReadyToRunMethodSigFlags Flags { get; }

    /// <summary>
    /// Module index from UpdateContext, or -1 if the method is in the current module.
    /// </summary>
    public int ModuleIndex { get; }

    /// <summary>
    /// Owner type override, or null if the signature did not include an explicit owner type.
    /// </summary>
    public R2RTypeNode OwnerType { get; }

    /// <summary>
    /// Whether the token refers to a MemberReference (true) or MethodDefinition (false).
    /// </summary>
    public bool IsMemberRef { get; }

    /// <summary>
    /// RID of the method token (MethodDef or MemberRef).
    /// </summary>
    public int Rid { get; }

    /// <summary>
    /// Generic method type arguments, or empty if the method is not a generic instantiation.
    /// </summary>
    public ImmutableArray<R2RTypeNode> TypeArguments { get; }

    /// <summary>
    /// Constrained type for constrained call, or null if not constrained.
    /// </summary>
    public R2RTypeNode ConstrainedType { get; }

    public R2RMethodRef(
        ReadyToRunMethodSigFlags flags,
        int moduleIndex,
        R2RTypeNode ownerType,
        bool isMemberRef,
        int rid,
        ImmutableArray<R2RTypeNode> typeArguments,
        R2RTypeNode constrainedType)
    {
        Flags = flags;
        ModuleIndex = moduleIndex;
        OwnerType = ownerType;
        IsMemberRef = isMemberRef;
        Rid = rid;
        TypeArguments = typeArguments;
        ConstrainedType = constrainedType;
    }

    public HandleKind TokenKind => IsMemberRef ? HandleKind.MemberReference : HandleKind.MethodDefinition;

    public void AppendTo(StringBuilder sb)
    {
        if ((Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub) != 0)
            sb.Append("[UNBOX] ");
        if ((Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub) != 0)
            sb.Append("[INST] ");
        if ((Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant) != 0)
            sb.Append("[ASYNC] ");

        if (ConstrainedType != null)
        {
            sb.Append("constrained(");
            ConstrainedType.AppendTo(sb);
            sb.Append(") ");
        }

        if (OwnerType != null)
        {
            OwnerType.AppendTo(sb);
            sb.Append("::");
        }

        if (ModuleIndex >= 0)
            sb.Append($"[module:{ModuleIndex}]");

        string kindName = IsMemberRef ? "MemberRef" : "MethodDef";
        sb.Append($"{kindName}:{Rid:X6}");

        if (!TypeArguments.IsEmpty)
        {
            sb.Append('<');
            for (int i = 0; i < TypeArguments.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                TypeArguments[i].AppendTo(sb);
            }
            sb.Append('>');
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }
}
