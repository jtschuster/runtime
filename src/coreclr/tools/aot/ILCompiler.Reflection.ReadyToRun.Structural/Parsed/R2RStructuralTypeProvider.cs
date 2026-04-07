// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Internal.ReadyToRunConstants;

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural.Parsed;

/// <summary>
/// Context for the structural signature decoder. Tracks the mapping from
/// MetadataReaders to module indices so that types from different assemblies
/// can be tagged with their source module.
/// </summary>
public sealed class R2RStructuralContext
{
    private readonly Dictionary<MetadataReader, int> _readerToModuleIndex;

    public R2RStructuralContext(Dictionary<MetadataReader, int> readerToModuleIndex)
    {
        _readerToModuleIndex = readerToModuleIndex;
    }

    /// <summary>
    /// Gets the module index for a MetadataReader, or -1 if it's the default (current) module.
    /// </summary>
    public int GetModuleIndex(MetadataReader reader)
    {
        if (_readerToModuleIndex != null && _readerToModuleIndex.TryGetValue(reader, out int index))
            return index;

        return -1;
    }
}

/// <summary>
/// A structural implementation of <see cref="IR2RSignatureTypeProvider{TType, TMethod, TGenericContext}"/>
/// that produces AST nodes (<see cref="R2RTypeNode"/> and <see cref="R2RMethodRef"/>) instead of
/// human-readable strings. This allows signature data to be captured without metadata name resolution.
/// </summary>
public sealed class R2RStructuralTypeProvider
    : IR2RSignatureTypeProvider<R2RTypeNode, R2RMethodRef, R2RStructuralContext>
{
    public static readonly R2RStructuralTypeProvider Instance = new R2RStructuralTypeProvider();

    private R2RStructuralTypeProvider() { }

    // --- Primitive and simple types ---

    public R2RTypeNode GetPrimitiveType(PrimitiveTypeCode typeCode)
        => new R2RPrimitiveTypeNode(typeCode);

    public R2RTypeNode GetCanonType()
        => R2RCanonTypeNode.Instance;

    // --- Token-based types ---

    public R2RTypeNode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind = 0)
    {
        bool isValueType = rawTypeKind == (byte)SignatureTypeKind.ValueType;
        return new R2RTokenTypeNode(-1, HandleKind.TypeDefinition, MetadataTokens.GetRowNumber(handle), isValueType);
    }

    public R2RTypeNode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind = 0)
    {
        bool isValueType = rawTypeKind == (byte)SignatureTypeKind.ValueType;
        return new R2RTokenTypeNode(-1, HandleKind.TypeReference, MetadataTokens.GetRowNumber(handle), isValueType);
    }

    public R2RTypeNode GetTypeFromSpecification(MetadataReader reader, R2RStructuralContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind = 0)
    {
        // TypeSpec blobs are self-contained type signatures — the decoder will parse the inner type
        // through the specification's signature blob. We just return a token node here.
        return new R2RTokenTypeNode(-1, HandleKind.TypeSpecification, MetadataTokens.GetRowNumber(handle), false);
    }

    // --- Composite types ---

    public R2RTypeNode GetPointerType(R2RTypeNode elementType)
        => new R2RPointerTypeNode(elementType);

    public R2RTypeNode GetByReferenceType(R2RTypeNode elementType)
        => new R2RByRefTypeNode(elementType);

    public R2RTypeNode GetSZArrayType(R2RTypeNode elementType)
        => new R2RSzArrayTypeNode(elementType);

    public R2RTypeNode GetArrayType(R2RTypeNode elementType, ArrayShape shape)
        => new R2RArrayTypeNode(elementType, shape);

    public R2RTypeNode GetGenericInstantiation(R2RTypeNode genericType, ImmutableArray<R2RTypeNode> typeArguments)
        => new R2RGenericInstTypeNode(genericType, typeArguments);

    public R2RTypeNode GetPinnedType(R2RTypeNode elementType)
        => new R2RPinnedTypeNode(elementType);

    public R2RTypeNode GetModifiedType(R2RTypeNode modifier, R2RTypeNode unmodifiedType, bool isRequired)
        => new R2RModifiedTypeNode(modifier, unmodifiedType, isRequired);

    public R2RTypeNode GetFunctionPointerType(MethodSignature<R2RTypeNode> signature)
        => new R2RFunctionPointerTypeNode(signature);

    // --- Generic parameters ---

    public R2RTypeNode GetGenericTypeParameter(R2RStructuralContext genericContext, int index)
        => new R2RGenericTypeParamNode(index);

    public R2RTypeNode GetGenericMethodParameter(R2RStructuralContext genericContext, int index)
        => new R2RGenericMethodParamNode(index);

    // --- Method references ---

    public R2RMethodRef GetMethodFromMethodDef(MetadataReader reader, MethodDefinitionHandle handle, R2RTypeNode owningTypeOverride)
    {
        return new R2RMethodRef(
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_None,
            moduleIndex: -1,
            ownerType: owningTypeOverride,
            isMemberRef: false,
            rid: MetadataTokens.GetRowNumber(handle),
            typeArguments: ImmutableArray<R2RTypeNode>.Empty,
            constrainedType: null);
    }

    public R2RMethodRef GetMethodFromMemberRef(MetadataReader reader, MemberReferenceHandle handle, R2RTypeNode owningTypeOverride)
    {
        return new R2RMethodRef(
            ReadyToRunMethodSigFlags.READYTORUN_METHOD_SIG_None,
            moduleIndex: -1,
            ownerType: owningTypeOverride,
            isMemberRef: true,
            rid: MetadataTokens.GetRowNumber(handle),
            typeArguments: ImmutableArray<R2RTypeNode>.Empty,
            constrainedType: null);
    }

    public R2RMethodRef GetInstantiatedMethod(R2RMethodRef uninstantiatedMethod, ImmutableArray<R2RTypeNode> instantiation)
    {
        return new R2RMethodRef(
            uninstantiatedMethod.Flags,
            uninstantiatedMethod.ModuleIndex,
            uninstantiatedMethod.OwnerType,
            uninstantiatedMethod.IsMemberRef,
            uninstantiatedMethod.Rid,
            typeArguments: instantiation,
            uninstantiatedMethod.ConstrainedType);
    }

    public R2RMethodRef GetConstrainedMethod(R2RMethodRef method, R2RTypeNode constraint)
    {
        return new R2RMethodRef(
            method.Flags,
            method.ModuleIndex,
            method.OwnerType,
            method.IsMemberRef,
            method.Rid,
            method.TypeArguments,
            constrainedType: constraint);
    }

    public R2RMethodRef GetMethodWithFlags(ReadyToRunMethodSigFlags flags, R2RMethodRef method)
    {
        return new R2RMethodRef(
            method.Flags | flags,
            method.ModuleIndex,
            method.OwnerType,
            method.IsMemberRef,
            method.Rid,
            method.TypeArguments,
            method.ConstrainedType);
    }
}
