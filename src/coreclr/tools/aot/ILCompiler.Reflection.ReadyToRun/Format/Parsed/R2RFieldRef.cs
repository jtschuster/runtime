// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.Metadata;
using System.Text;
using Internal.ReadyToRunConstants;

namespace ILCompiler.Reflection.ReadyToRun.Format.Parsed;

/// <summary>
/// Structural representation of a field reference in an R2R signature.
/// Stores the raw token and flags without resolving field names from metadata.
/// </summary>
public sealed class R2RFieldRef
{
    /// <summary>
    /// Flags from the field signature encoding.
    /// </summary>
    public ReadyToRunFieldSigFlags Flags { get; }

    /// <summary>
    /// Owner type override, or null if the signature did not include an explicit owner type.
    /// </summary>
    public R2RTypeNode OwnerType { get; }

    /// <summary>
    /// Whether the token refers to a MemberReference (true) or FieldDefinition (false).
    /// </summary>
    public bool IsMemberRef { get; }

    /// <summary>
    /// RID of the field token (FieldDef or MemberRef).
    /// </summary>
    public int Rid { get; }

    public R2RFieldRef(ReadyToRunFieldSigFlags flags, R2RTypeNode ownerType, bool isMemberRef, int rid)
    {
        Flags = flags;
        OwnerType = ownerType;
        IsMemberRef = isMemberRef;
        Rid = rid;
    }

    public HandleKind TokenKind => IsMemberRef ? HandleKind.MemberReference : HandleKind.FieldDefinition;

    public void AppendTo(StringBuilder sb)
    {
        if (OwnerType != null)
        {
            OwnerType.AppendTo(sb);
            sb.Append("::");
        }

        string kindName = IsMemberRef ? "MemberRef" : "FieldDef";
        sb.Append($"{kindName}:{Rid:X6}");
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }
}
