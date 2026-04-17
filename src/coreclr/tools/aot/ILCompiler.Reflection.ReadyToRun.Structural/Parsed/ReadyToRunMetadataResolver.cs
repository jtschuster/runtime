// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;


namespace ILCompiler.Reflection.ReadyToRun.Parsed;

/// <summary>
/// Resolves structural AST tokens (<see cref="R2RTypeNode"/>, <see cref="R2RMethodRef"/>,
/// <see cref="R2RFieldRef"/>) into human-readable strings using ECMA metadata.
///
/// <para>
/// This is the third layer of the R2R analysis stack:
/// <list type="number">
///   <item><see cref="ReadyToRunReader"/> — raw section tables</item>
///   <item><see cref="RawReadyToRunParser"/> — cross-referenced structural ASTs</item>
///   <item><see cref="ReadyToRunMetadataResolver"/> — name resolution (this class)</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReadyToRunMetadataResolver
{
    private readonly ReadyToRunReader _reader;

    public ReadyToRunMetadataResolver(ReadyToRunReader reader)
    {
        _reader = reader;
    }

    // ── Public resolve methods ───────────────────────────────────────

    /// <summary>
    /// Resolve a type AST node into a human-readable string.
    /// </summary>
    public string ResolveType(R2RTypeNode type)
    {
        if (type is null)
            return "???";

        var sb = new StringBuilder();
        AppendType(sb, type);
        return sb.ToString();
    }

    /// <summary>
    /// Resolve a method reference AST into a human-readable string.
    /// </summary>
    public string ResolveMethod(R2RMethodRef method)
    {
        if (method is null)
            return "???";

        var sb = new StringBuilder();
        AppendMethod(sb, method);
        return sb.ToString();
    }

    /// <summary>
    /// Resolve a field reference AST into a human-readable string.
    /// </summary>
    public string ResolveField(R2RFieldRef field)
    {
        if (field is null)
            return "???";

        var sb = new StringBuilder();
        AppendField(sb, field);
        return sb.ToString();
    }

    /// <summary>
    /// Resolve a fixup signature into a human-readable string.
    /// </summary>
    public string ResolveFixupSignature(R2RFixupSignature sig)
    {
        if (sig is null)
            return "???";

        var sb = new StringBuilder();
        AppendFixupSignature(sb, sig);
        return sb.ToString();
    }

    /// <summary>
    /// Resolve a MethodDef RID to a human-readable method name.
    /// Uses the global metadata (module index 0).
    /// </summary>
    public string ResolveMethodDef(uint rid, int moduleIndex = -1)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(moduleIndex);
            if (reader is null)
                return $"MethodDef:{rid:X6}";

            var handle = MetadataTokens.MethodDefinitionHandle((int)rid);
            return MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true);
        }
        catch
        {
            return $"MethodDef:{rid:X6}";
        }
    }

    /// <summary>
    /// Resolve a TypeDef RID to a human-readable type name.
    /// </summary>
    public string ResolveTypeDef(uint rid, int moduleIndex = -1)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(moduleIndex);
            if (reader is null)
                return $"TypeDef:{rid:X6}";

            var handle = MetadataTokens.TypeDefinitionHandle((int)rid);
            return MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true);
        }
        catch
        {
            return $"TypeDef:{rid:X6}";
        }
    }

    /// <summary>
    /// Resolve an ExportedType RID to a human-readable type name.
    /// </summary>
    public string ResolveExportedType(uint rid, int moduleIndex = -1)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(moduleIndex);
            if (reader is null)
                return $"ExportedType:{rid:X6}";

            var handle = MetadataTokens.ExportedTypeHandle((int)rid);
            var exportedType = reader.GetExportedType(handle);
            string ns = reader.GetString(exportedType.Namespace);
            string name = reader.GetString(exportedType.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        catch
        {
            return $"ExportedType:{rid:X6}";
        }
    }

    /// <summary>
    /// Resolve a UserString RID to the actual string value.
    /// </summary>
    public string ResolveUserString(int rid, int moduleIndex = -1)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(moduleIndex);
            if (reader is null)
                return $"UserString:{rid:X6}";

            var handle = MetadataTokens.UserStringHandle(rid);
            return reader.GetUserString(handle);
        }
        catch
        {
            return $"UserString:{rid:X6}";
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────

    private MetadataReader GetMetadataReader(int moduleIndex)
    {
        if (moduleIndex < 0)
        {
            return _reader.GetGlobalMetadata()?.MetadataReader;
        }

        try
        {
            return _reader.OpenReferenceAssembly(moduleIndex)?.MetadataReader;
        }
        catch
        {
            return null;
        }
    }

    // ── Type resolution ──────────────────────────────────────────────

    private void AppendType(StringBuilder sb, R2RTypeNode type)
    {
        switch (type)
        {
            case R2RPrimitiveTypeNode prim:
                type.AppendTo(sb);
                break;

            case R2RTokenTypeNode token:
                AppendTokenType(sb, token);
                break;

            case R2RPointerTypeNode ptr:
                AppendType(sb, ptr.ElementType);
                sb.Append('*');
                break;

            case R2RByRefTypeNode byRef:
                AppendType(sb, byRef.ElementType);
                sb.Append('&');
                break;

            case R2RSzArrayTypeNode szArr:
                AppendType(sb, szArr.ElementType);
                sb.Append("[]");
                break;

            case R2RArrayTypeNode arr:
                AppendType(sb, arr.ElementType);
                sb.Append('[');
                for (int i = 1; i < arr.Shape.Rank; i++)
                    sb.Append(',');
                sb.Append(']');
                break;

            case R2RGenericInstTypeNode genInst:
                AppendType(sb, genInst.GenericType);
                sb.Append('<');
                for (int i = 0; i < genInst.TypeArguments.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    AppendType(sb, genInst.TypeArguments[i]);
                }
                sb.Append('>');
                break;

            case R2RGenericTypeParamNode gtp:
                sb.Append($"!{gtp.Index}");
                break;

            case R2RGenericMethodParamNode gmp:
                sb.Append($"!!{gmp.Index}");
                break;

            case R2RFunctionPointerTypeNode fnPtr:
                sb.Append("fnptr(");
                AppendType(sb, fnPtr.Signature.ReturnType);
                sb.Append('(');
                for (int i = 0; i < fnPtr.Signature.ParameterTypes.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    AppendType(sb, fnPtr.Signature.ParameterTypes[i]);
                }
                sb.Append("))");
                break;

            case R2RModifiedTypeNode mod:
                AppendType(sb, mod.UnmodifiedType);
                sb.Append(mod.IsRequired ? " modreq(" : " modopt(");
                AppendType(sb, mod.Modifier);
                sb.Append(')');
                break;

            case R2RPinnedTypeNode pinned:
                sb.Append("pinned ");
                AppendType(sb, pinned.ElementType);
                break;

            case R2RCanonTypeNode:
                sb.Append("__Canon");
                break;

            case R2RModuleQualifiedTypeNode modQual:
                AppendType(sb, modQual.InnerType);
                break;

            default:
                type.AppendTo(sb);
                break;
        }
    }

    private void AppendTokenType(StringBuilder sb, R2RTokenTypeNode token)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(token.ModuleIndex);
            if (reader is null)
            {
                token.AppendTo(sb);
                return;
            }

            EntityHandle handle = token.TokenKind switch
            {
                HandleKind.TypeDefinition => MetadataTokens.TypeDefinitionHandle(token.Rid),
                HandleKind.TypeReference => MetadataTokens.TypeReferenceHandle(token.Rid),
                HandleKind.TypeSpecification => MetadataTokens.TypeSpecificationHandle(token.Rid),
                _ => default,
            };

            if (handle.IsNil)
            {
                token.AppendTo(sb);
                return;
            }

            sb.Append(MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true));
        }
        catch
        {
            token.AppendTo(sb);
        }
    }

    // ── Method resolution ────────────────────────────────────────────

    private void AppendMethod(StringBuilder sb, R2RMethodRef method)
    {
        if ((method.Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_UnboxingStub) != 0)
            sb.Append("[UNBOX] ");
        if ((method.Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_InstantiatingStub) != 0)
            sb.Append("[INST] ");
        if ((method.Flags & ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_AsyncVariant) != 0)
            sb.Append("[ASYNC] ");

        if (method.ConstrainedType is not null)
        {
            sb.Append("constrained(");
            AppendType(sb, method.ConstrainedType);
            sb.Append(") ");
        }

        try
        {
            int moduleIndex = method.ModuleIndex;
            MetadataReader reader = GetMetadataReader(moduleIndex);

            if (reader is null)
            {
                AppendMethodFallback(sb, method);
                return;
            }

            EntityHandle handle = method.IsMemberRef
                ? MetadataTokens.MemberReferenceHandle(method.Rid)
                : (EntityHandle)MetadataTokens.MethodDefinitionHandle(method.Rid);

            string owningTypeOverride = null;
            if (method.OwnerType is not null)
                owningTypeOverride = ResolveType(method.OwnerType);

            sb.Append(MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true, owningTypeOverride: owningTypeOverride));
        }
        catch
        {
            AppendMethodFallback(sb, method);
        }

        if (!method.TypeArguments.IsEmpty)
        {
            sb.Append('<');
            for (int i = 0; i < method.TypeArguments.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                AppendType(sb, method.TypeArguments[i]);
            }
            sb.Append('>');
        }
    }

    private void AppendMethodFallback(StringBuilder sb, R2RMethodRef method)
    {
        if (method.OwnerType is not null)
        {
            AppendType(sb, method.OwnerType);
            sb.Append("::");
        }

        string kindName = method.IsMemberRef ? "MemberRef" : "MethodDef";
        sb.Append($"{kindName}:{method.Rid:X6}");
    }

    // ── Field resolution ─────────────────────────────────────────────

    private void AppendField(StringBuilder sb, R2RFieldRef field)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(-1); // fields always from current module
            if (reader is null)
            {
                field.AppendTo(sb);
                return;
            }

            EntityHandle handle = field.IsMemberRef
                ? MetadataTokens.MemberReferenceHandle(field.Rid)
                : (EntityHandle)MetadataTokens.FieldDefinitionHandle(field.Rid);

            string owningTypeOverride = null;
            if (field.OwnerType is not null)
                owningTypeOverride = ResolveType(field.OwnerType);

            sb.Append(MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true, owningTypeOverride: owningTypeOverride));
        }
        catch
        {
            field.AppendTo(sb);
        }
    }

    // ── Fixup signature resolution ───────────────────────────────────

    private void AppendFixupSignature(StringBuilder sb, R2RFixupSignature sig)
    {
        sb.Append(sig.Kind.ToString());
        if (sig.ModuleIndex >= 0)
            sb.Append($" [module:{sig.ModuleIndex}]");
        sb.Append(' ');

        if (sig.Payload is null)
        {
            sb.Append("<no payload>");
            return;
        }

        switch (sig.Payload)
        {
            case R2RTypeFixupPayload typePayload:
                AppendType(sb, typePayload.Type);
                break;

            case R2RMethodFixupPayload methodPayload:
                AppendMethod(sb, methodPayload.Method);
                break;

            case R2RFieldFixupPayload fieldPayload:
                AppendField(sb, fieldPayload.Field);
                break;

            case R2RFieldOffsetValueFixupPayload fieldOffsetPayload:
                AppendField(sb, fieldOffsetPayload.Field);
                break;

            case R2RTokenFixupPayload tokenPayload:
                AppendToken(sb, tokenPayload);
                break;

            case R2RHelperFixupPayload helperPayload:
                sb.Append(helperPayload.HelperId.ToString());
                break;

            case R2RStringFixupPayload stringPayload:
                sb.Append('"');
                sb.Append(ResolveUserString(stringPayload.Rid));
                sb.Append('"');
                break;

            case R2RDictionaryLookupFixupPayload dictPayload:
                if (dictPayload.LookupType is not null)
                {
                    sb.Append("context:");
                    AppendType(sb, dictPayload.LookupType);
                    sb.Append(' ');
                }
                sb.Append("lookup:");
                AppendFixupSignature(sb, dictPayload.InnerSignature);
                break;

            case R2RTypeLayoutFixupPayload layoutPayload:
                AppendType(sb, layoutPayload.Type);
                sb.Append($" size:0x{layoutPayload.Size:X} align:0x{layoutPayload.Alignment:X}");
                break;

            case R2RFieldOffsetFixupPayload fieldOffCheck:
                sb.Append($"offset:0x{fieldOffCheck.ExpectedOffset:X} ");
                AppendField(sb, fieldOffCheck.Field);
                break;

            case R2RVirtualOverrideFixupPayload overridePayload:
                sb.Append("decl:");
                AppendMethod(sb, overridePayload.Declaration);
                sb.Append(" implType:");
                AppendType(sb, overridePayload.ImplementationType);
                if (overridePayload.ImplementationMethod is not null)
                {
                    sb.Append(" impl:");
                    AppendMethod(sb, overridePayload.ImplementationMethod);
                }
                break;

            case R2RInstructionSetFixupPayload isPayload:
                isPayload.AppendTo(sb);
                break;

            case R2RILBodyFixupPayload ilPayload:
                AppendMethod(sb, ilPayload.Method);
                sb.Append($" il:{ilPayload.ILBytes.Length}bytes");
                break;

            case R2RDelegateCtorFixupPayload delegatePayload:
                sb.Append("target:");
                AppendMethod(sb, delegatePayload.TargetMethod);
                sb.Append(" delegate:");
                AppendType(sb, delegatePayload.DelegateType);
                break;

            case R2RSlotFixupPayload slotPayload:
                sb.Append($"slot:{slotPayload.Slot} ");
                AppendType(sb, slotPayload.Type);
                break;

            case R2RStubEntryPointFixupPayload stubPayload:
                sb.Append($"RVA:0x{stubPayload.Rva:X8}");
                break;

            default:
                sig.Payload.AppendTo(sb);
                break;
        }
    }

    private void AppendToken(StringBuilder sb, R2RTokenFixupPayload token)
    {
        try
        {
            MetadataReader reader = GetMetadataReader(-1);
            if (reader is null)
            {
                token.AppendTo(sb);
                return;
            }

            EntityHandle handle = token.IsMemberRef
                ? MetadataTokens.MemberReferenceHandle(token.Rid)
                : (EntityHandle)MetadataTokens.MethodDefinitionHandle(token.Rid);

            sb.Append(MetadataNameFormatter.FormatHandle(reader, handle, namespaceQualified: true));
        }
        catch
        {
            token.AppendTo(sb);
        }
    }
}
