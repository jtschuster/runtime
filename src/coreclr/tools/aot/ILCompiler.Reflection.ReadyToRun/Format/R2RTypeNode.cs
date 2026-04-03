// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace ILCompiler.Reflection.ReadyToRun.Format;

/// <summary>
/// Structural representation of a type signature in an R2R image.
/// Stores raw tokens (module index, handle kind, RID) without metadata name resolution.
/// </summary>
public abstract class R2RTypeNode
{
    public abstract R2RTypeNodeKind Kind { get; }

    public abstract void AppendTo(StringBuilder sb);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }
}

public enum R2RTypeNodeKind
{
    Primitive,
    Token,
    Pointer,
    ByRef,
    SzArray,
    Array,
    GenericInst,
    GenericTypeParam,
    GenericMethodParam,
    FunctionPointer,
    Modified,
    Pinned,
    Canon,
    ModuleQualified,
}

/// <summary>
/// Primitive type (void, bool, char, int8..uint64, float, double, string, object, IntPtr, UIntPtr, TypedReference).
/// </summary>
public sealed class R2RPrimitiveTypeNode : R2RTypeNode
{
    public PrimitiveTypeCode Code { get; }

    public R2RPrimitiveTypeNode(PrimitiveTypeCode code) => Code = code;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Primitive;

    public override void AppendTo(StringBuilder sb) => sb.Append(Code switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "int8",
        PrimitiveTypeCode.Byte => "uint8",
        PrimitiveTypeCode.Int16 => "int16",
        PrimitiveTypeCode.UInt16 => "uint16",
        PrimitiveTypeCode.Int32 => "int32",
        PrimitiveTypeCode.UInt32 => "uint32",
        PrimitiveTypeCode.Int64 => "int64",
        PrimitiveTypeCode.UInt64 => "uint64",
        PrimitiveTypeCode.Single => "float32",
        PrimitiveTypeCode.Double => "float64",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => $"Primitive({Code})",
    });
}

/// <summary>
/// Type referenced by token (VALUETYPE or CLASS element type).
/// The module index indicates which assembly the token comes from (-1 = current module).
/// </summary>
public sealed class R2RTokenTypeNode : R2RTypeNode
{
    public int ModuleIndex { get; }
    public HandleKind TokenKind { get; }
    public int Rid { get; }
    public bool IsValueType { get; }

    public R2RTokenTypeNode(int moduleIndex, HandleKind tokenKind, int rid, bool isValueType)
    {
        ModuleIndex = moduleIndex;
        TokenKind = tokenKind;
        Rid = rid;
        IsValueType = isValueType;
    }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Token;

    public override void AppendTo(StringBuilder sb)
    {
        if (ModuleIndex >= 0)
            sb.Append($"[module:{ModuleIndex}]");

        string kindName = TokenKind switch
        {
            HandleKind.TypeDefinition => "TypeDef",
            HandleKind.TypeReference => "TypeRef",
            HandleKind.TypeSpecification => "TypeSpec",
            _ => TokenKind.ToString(),
        };
        sb.Append(IsValueType ? "valuetype " : "class ");
        sb.Append($"{kindName}:{Rid:X6}");
    }
}

/// <summary>
/// Pointer type (PTR).
/// </summary>
public sealed class R2RPointerTypeNode : R2RTypeNode
{
    public R2RTypeNode ElementType { get; }

    public R2RPointerTypeNode(R2RTypeNode elementType) => ElementType = elementType;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Pointer;

    public override void AppendTo(StringBuilder sb)
    {
        ElementType.AppendTo(sb);
        sb.Append('*');
    }
}

/// <summary>
/// By-reference type (BYREF).
/// </summary>
public sealed class R2RByRefTypeNode : R2RTypeNode
{
    public R2RTypeNode ElementType { get; }

    public R2RByRefTypeNode(R2RTypeNode elementType) => ElementType = elementType;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.ByRef;

    public override void AppendTo(StringBuilder sb)
    {
        ElementType.AppendTo(sb);
        sb.Append('&');
    }
}

/// <summary>
/// Single-dimensional zero-indexed array type (SZARRAY).
/// </summary>
public sealed class R2RSzArrayTypeNode : R2RTypeNode
{
    public R2RTypeNode ElementType { get; }

    public R2RSzArrayTypeNode(R2RTypeNode elementType) => ElementType = elementType;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.SzArray;

    public override void AppendTo(StringBuilder sb)
    {
        ElementType.AppendTo(sb);
        sb.Append("[]");
    }
}

/// <summary>
/// Multi-dimensional array type (ARRAY).
/// </summary>
public sealed class R2RArrayTypeNode : R2RTypeNode
{
    public R2RTypeNode ElementType { get; }
    public ArrayShape Shape { get; }

    public R2RArrayTypeNode(R2RTypeNode elementType, ArrayShape shape)
    {
        ElementType = elementType;
        Shape = shape;
    }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Array;

    public override void AppendTo(StringBuilder sb)
    {
        ElementType.AppendTo(sb);
        sb.Append('[');
        for (int i = 0; i < Shape.Rank; i++)
        {
            if (i > 0)
                sb.Append(',');
        }
        sb.Append(']');
    }
}

/// <summary>
/// Generic instantiation (GENERICINST).
/// </summary>
public sealed class R2RGenericInstTypeNode : R2RTypeNode
{
    public R2RTypeNode GenericType { get; }
    public ImmutableArray<R2RTypeNode> TypeArguments { get; }

    public R2RGenericInstTypeNode(R2RTypeNode genericType, ImmutableArray<R2RTypeNode> typeArguments)
    {
        GenericType = genericType;
        TypeArguments = typeArguments;
    }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.GenericInst;

    public override void AppendTo(StringBuilder sb)
    {
        GenericType.AppendTo(sb);
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

/// <summary>
/// Generic type parameter (VAR).
/// </summary>
public sealed class R2RGenericTypeParamNode : R2RTypeNode
{
    public int Index { get; }

    public R2RGenericTypeParamNode(int index) => Index = index;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.GenericTypeParam;

    public override void AppendTo(StringBuilder sb) => sb.Append($"!{Index}");
}

/// <summary>
/// Generic method parameter (MVAR).
/// </summary>
public sealed class R2RGenericMethodParamNode : R2RTypeNode
{
    public int Index { get; }

    public R2RGenericMethodParamNode(int index) => Index = index;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.GenericMethodParam;

    public override void AppendTo(StringBuilder sb) => sb.Append($"!!{Index}");
}

/// <summary>
/// Function pointer type (FNPTR).
/// </summary>
public sealed class R2RFunctionPointerTypeNode : R2RTypeNode
{
    public MethodSignature<R2RTypeNode> Signature { get; }

    public R2RFunctionPointerTypeNode(MethodSignature<R2RTypeNode> signature) => Signature = signature;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.FunctionPointer;

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append("fnptr(");
        Signature.ReturnType.AppendTo(sb);
        sb.Append('(');
        for (int i = 0; i < Signature.ParameterTypes.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            Signature.ParameterTypes[i].AppendTo(sb);
        }
        sb.Append("))");
    }
}

/// <summary>
/// Modified type (CMOD_REQD or CMOD_OPT).
/// </summary>
public sealed class R2RModifiedTypeNode : R2RTypeNode
{
    public R2RTypeNode Modifier { get; }
    public R2RTypeNode UnmodifiedType { get; }
    public bool IsRequired { get; }

    public R2RModifiedTypeNode(R2RTypeNode modifier, R2RTypeNode unmodifiedType, bool isRequired)
    {
        Modifier = modifier;
        UnmodifiedType = unmodifiedType;
        IsRequired = isRequired;
    }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Modified;

    public override void AppendTo(StringBuilder sb)
    {
        UnmodifiedType.AppendTo(sb);
        sb.Append(IsRequired ? " modreq(" : " modopt(");
        Modifier.AppendTo(sb);
        sb.Append(')');
    }
}

/// <summary>
/// Pinned type (PINNED).
/// </summary>
public sealed class R2RPinnedTypeNode : R2RTypeNode
{
    public R2RTypeNode ElementType { get; }

    public R2RPinnedTypeNode(R2RTypeNode elementType) => ElementType = elementType;

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Pinned;

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append("pinned ");
        ElementType.AppendTo(sb);
    }
}

/// <summary>
/// Canon type (CANON_ZAPSIG — __Canon placeholder for generic sharing).
/// </summary>
public sealed class R2RCanonTypeNode : R2RTypeNode
{
    public static R2RCanonTypeNode Instance { get; } = new();

    private R2RCanonTypeNode() { }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.Canon;

    public override void AppendTo(StringBuilder sb) => sb.Append("__Canon");
}

/// <summary>
/// Module-qualified type (MODULE_ZAPSIG — type from a different module in the version bubble).
/// </summary>
public sealed class R2RModuleQualifiedTypeNode : R2RTypeNode
{
    public int ModuleIndex { get; }
    public R2RTypeNode InnerType { get; }

    public R2RModuleQualifiedTypeNode(int moduleIndex, R2RTypeNode innerType)
    {
        ModuleIndex = moduleIndex;
        InnerType = innerType;
    }

    public override R2RTypeNodeKind Kind => R2RTypeNodeKind.ModuleQualified;

    public override void AppendTo(StringBuilder sb)
    {
        sb.Append($"[module:{ModuleIndex}]");
        InnerType.AppendTo(sb);
    }
}
