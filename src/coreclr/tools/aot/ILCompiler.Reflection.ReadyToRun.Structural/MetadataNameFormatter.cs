// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace ILCompiler.Reflection.ReadyToRun.Structural;

    public class MetadataNameFormatter : DisassemblingTypeProvider
    {
        /// <summary>
        /// Metadata reader used for the purpose of metadata-based name formatting.
        /// </summary>
        private readonly MetadataReader _metadataReader;

        public MetadataNameFormatter(MetadataReader metadataReader)
        {
            _metadataReader = metadataReader;
        }

        /// <summary>
        /// Construct the textual representation of a given metadata handle.
        /// </summary>
        /// <param name="metadataReader">Metadata reader corresponding to the handle</param>
        /// <param name="handle">Metadata handle to parse</param>
        /// <param name="namespaceQualified">Include namespace in type names</param>
        public static string FormatHandle(MetadataReader metadataReader, Handle handle, bool namespaceQualified = true, string owningTypeOverride = null, string signaturePrefix = "")
        {
            MetadataNameFormatter formatter = new MetadataNameFormatter(metadataReader);
            return formatter.EmitHandleName(handle, namespaceQualified, owningTypeOverride, signaturePrefix);
        }


        /// <summary>
        /// Emit a given token to a specified string builder.
        /// </summary>
        /// <param name="methodToken">ECMA token to provide string representation for</param>
        private string EmitHandleName(Handle handle, bool namespaceQualified, string owningTypeOverride, string signaturePrefix = "")
        {
            try
            {
                switch (handle.Kind)
                {
                    case HandleKind.MemberReference:
                        return EmitMemberReferenceName((MemberReferenceHandle)handle, owningTypeOverride, signaturePrefix);

                    case HandleKind.MethodSpecification:
                        return EmitMethodSpecificationName((MethodSpecificationHandle)handle, owningTypeOverride, signaturePrefix);

                    case HandleKind.MethodDefinition:
                        return EmitMethodDefinitionName((MethodDefinitionHandle)handle, owningTypeOverride, signaturePrefix);

                    case HandleKind.TypeReference:
                        return EmitTypeReferenceName((TypeReferenceHandle)handle, namespaceQualified, signaturePrefix);

                    case HandleKind.TypeSpecification:
                        return EmitTypeSpecificationName((TypeSpecificationHandle)handle, namespaceQualified, signaturePrefix);

                    case HandleKind.TypeDefinition:
                        return EmitTypeDefinitionName((TypeDefinitionHandle)handle, namespaceQualified, signaturePrefix);

                    case HandleKind.FieldDefinition:
                        return EmitFieldDefinitionName((FieldDefinitionHandle)handle, namespaceQualified, owningTypeOverride, signaturePrefix);

                    default:
                        throw new NotImplementedException();
                }
            }
            catch (Exception ex)
            {
                return $"$$INVALID-{handle.Kind}-{MetadataTokens.GetRowNumber((EntityHandle)handle):X6}: {ex.Message}";
            }
        }

        /// <summary>
        /// Check that the metadata handle has valid range in the appropriate table context.
        /// </summary>
        /// <param name="handle">Metadata handle to validate</param>
        private void ValidateHandle(EntityHandle handle, TableIndex tableIndex)
        {
            int rowid = MetadataTokens.GetRowNumber(handle);
            int tableRowCount = _metadataReader.GetTableRowCount(tableIndex);
            if (rowid <= 0 || rowid > tableRowCount)
            {
                throw new NotImplementedException($"Invalid handle {MetadataTokens.GetToken(handle):X8} in table {tableIndex.ToString()} ({tableRowCount} rows)");
            }
        }

        /// <summary>
        /// Emit a method specification.
        /// </summary>
        /// <param name="methodSpecHandle">Method specification handle</param>
        private string EmitMethodSpecificationName(MethodSpecificationHandle methodSpecHandle, string owningTypeOverride, string signaturePrefix)
        {
            ValidateHandle(methodSpecHandle, TableIndex.MethodSpec);
            MethodSpecification methodSpec = _metadataReader.GetMethodSpecification(methodSpecHandle);
            DisassemblingGenericContext genericContext = new DisassemblingGenericContext(Array.Empty<string>(), Array.Empty<string>());
            return EmitHandleName(methodSpec.Method, namespaceQualified: true, owningTypeOverride: owningTypeOverride, signaturePrefix: signaturePrefix)
                + methodSpec.DecodeSignature<string, DisassemblingGenericContext>(this, genericContext);
        }

        /// <summary>
        /// Emit a method reference.
        /// </summary>
        /// <param name="memberRefHandle">Member reference handle</param>
        private string EmitMemberReferenceName(MemberReferenceHandle memberRefHandle, string owningTypeOverride, string signaturePrefix)
        {
            ValidateHandle(memberRefHandle, TableIndex.MemberRef);
            MemberReference memberRef = _metadataReader.GetMemberReference(memberRefHandle);
            StringBuilder builder = new StringBuilder();
            DisassemblingGenericContext genericContext = new DisassemblingGenericContext(Array.Empty<string>(), Array.Empty<string>());
            switch (memberRef.GetKind())
            {
                case MemberReferenceKind.Field:
                    {
                        string fieldSig = memberRef.DecodeFieldSignature<string, DisassemblingGenericContext>(this, genericContext);
                        builder.Append(fieldSig);
                        builder.Append(" ");
                        builder.Append(EmitContainingTypeAndMemberName(memberRef, owningTypeOverride, signaturePrefix));
                        break;
                    }

                case MemberReferenceKind.Method:
                    {
                        MethodSignature<String> methodSig = memberRef.DecodeMethodSignature<string, DisassemblingGenericContext>(this, genericContext);
                        builder.Append(methodSig.ReturnType);
                        builder.Append(" ");
                        builder.Append(EmitContainingTypeAndMemberName(memberRef, owningTypeOverride, signaturePrefix));
                        builder.Append(EmitMethodSignature(methodSig));
                        break;
                    }

                default:
                    throw new NotImplementedException(memberRef.GetKind().ToString());
            }

            return builder.ToString();
        }

        /// <summary>
        /// Emit a method definition.
        /// </summary>
        /// <param name="methodSpecHandle">Method definition handle</param>
        private string EmitMethodDefinitionName(MethodDefinitionHandle methodDefinitionHandle, string owningTypeOverride, string signaturePrefix)
        {
            ValidateHandle(methodDefinitionHandle, TableIndex.MethodDef);
            MethodDefinition methodDef = _metadataReader.GetMethodDefinition(methodDefinitionHandle);
            DisassemblingGenericContext genericContext = new DisassemblingGenericContext(Array.Empty<string>(), Array.Empty<string>());
            MethodSignature<string> methodSig = methodDef.DecodeSignature<string, DisassemblingGenericContext>(this, genericContext);
            StringBuilder builder = new StringBuilder();
            builder.Append(methodSig.ReturnType);
            builder.Append(" ");
            if (owningTypeOverride == null)
            {
                owningTypeOverride = EmitHandleName(methodDef.GetDeclaringType(), namespaceQualified: true, owningTypeOverride: null);
            }
            builder.Append(owningTypeOverride);
            builder.Append(".");
            builder.Append(signaturePrefix);
            builder.Append(EmitString(methodDef.Name));
            builder.Append(EmitMethodSignature(methodSig));
            return builder.ToString();
        }

        /// <summary>
        /// Emit method generic arguments and parameter list.
        /// </summary>
        /// <param name="methodSignature">Method signature to format</param>
        private string EmitMethodSignature(MethodSignature<string> methodSignature)
        {
            StringBuilder builder = new StringBuilder();
            if (methodSignature.GenericParameterCount != 0)
            {
                builder.Append("<");
                bool firstTypeArg = true;
                for (int typeArgIndex = 0; typeArgIndex < methodSignature.GenericParameterCount; typeArgIndex++)
                {
                    if (firstTypeArg)
                    {
                        firstTypeArg = false;
                    }
                    else
                    {
                        builder.Append(", ");
                    }
                    builder.Append("!!");
                    builder.Append(typeArgIndex);
                }
                builder.Append(">");
            }
            builder.Append("(");
            bool firstMethodArg = true;
            foreach (string paramType in methodSignature.ParameterTypes)
            {
                if (firstMethodArg)
                {
                    firstMethodArg = false;
                }
                else
                {
                    builder.Append(", ");
                }
                builder.Append(paramType);
            }
            builder.Append(")");
            return builder.ToString();
        }

        /// <summary>
        /// Emit containing type and member name.
        /// </summary>
        /// <param name="memberRef">Member reference to format</param>
        /// <param name="owningTypeOverride">Optional override for the owning type, null = MemberReference.Parent</param>
        /// <param name="signaturePrefix">Optional member signature prefix</param>
        private string EmitContainingTypeAndMemberName(MemberReference memberRef, string owningTypeOverride, string signaturePrefix)
        {
            if (owningTypeOverride == null)
            {
                owningTypeOverride = EmitHandleName(memberRef.Parent, namespaceQualified: true, owningTypeOverride: null);
            }
            return owningTypeOverride + "." + signaturePrefix + EmitString(memberRef.Name);
        }

        /// <summary>
        /// Emit type reference.
        /// </summary>
        /// <param name="typeRefHandle">Type reference handle</param>
        /// <param name="namespaceQualified">When set to true, include namespace information</param>
        /// <param name="signaturePrefix">Optional type name signature prefix</param>
        private string EmitTypeReferenceName(TypeReferenceHandle typeRefHandle, bool namespaceQualified, string signaturePrefix)
        {
            ValidateHandle(typeRefHandle, TableIndex.TypeRef);
            TypeReference typeRef = _metadataReader.GetTypeReference(typeRefHandle);
            string typeName = EmitString(typeRef.Name);
            string output = "";
            if ((typeRef.ResolutionScope.Kind != HandleKind.AssemblyReference) && (typeRef.ResolutionScope.Kind != HandleKind.ModuleReference))
            {
                // Nested type - format enclosing type followed by the nested type
                return EmitHandleName(typeRef.ResolutionScope, namespaceQualified, owningTypeOverride: null) + "+" + typeName;
            }
            if (namespaceQualified)
            {
                output = EmitString(typeRef.Namespace);
                if (!string.IsNullOrEmpty(output))
                {
                    output += ".";
                }
            }
            return output + signaturePrefix + typeName;
        }

        /// <summary>
        /// Emit a type definition.
        /// </summary>
        /// <param name="typeDefHandle">Type definition handle</param>
        /// <param name="namespaceQualified">true = prefix type name with namespace information</param>
        /// <param name="signaturePrefix">Optional type name signature prefix</param>
        /// <returns></returns>
        private string EmitTypeDefinitionName(TypeDefinitionHandle typeDefHandle, bool namespaceQualified, string signaturePrefix)
        {
            ValidateHandle(typeDefHandle, TableIndex.TypeDef);
            TypeDefinition typeDef = _metadataReader.GetTypeDefinition(typeDefHandle);
            string typeName = signaturePrefix + EmitString(typeDef.Name);
            if (typeDef.IsNested)
            {
                // Nested type
                return EmitHandleName(typeDef.GetDeclaringType(), namespaceQualified, owningTypeOverride: null) + "+" + typeName;
            }

            string output;
            if (namespaceQualified)
            {
                output = EmitString(typeDef.Namespace);
                if (!string.IsNullOrEmpty(output))
                {
                    output += ".";
                }
            }
            else
            {
                output = "";
            }
            return output + typeName;
        }

        /// <summary>
        /// Emit an arbitrary type specification.
        /// </summary>
        /// <param name="typeSpecHandle">Type specification handle</param>
        /// <param name="namespaceQualified">When set to true, include namespace information</param>
        private string EmitTypeSpecificationName(TypeSpecificationHandle typeSpecHandle, bool namespaceQualified, string signaturePrefix)
        {
            ValidateHandle(typeSpecHandle, TableIndex.TypeSpec);
            TypeSpecification typeSpec = _metadataReader.GetTypeSpecification(typeSpecHandle);
            DisassemblingGenericContext genericContext = new DisassemblingGenericContext(Array.Empty<string>(), Array.Empty<string>());
            return typeSpec.DecodeSignature<string, DisassemblingGenericContext>(this, genericContext);
        }

        /// <summary>
        /// Emit the textual representation of a FieldDef metadata record.
        /// </summary>
        /// <param name="fieldDefHandle">Field definition handle to format</param>
        /// <param name="namespaceQualified">True = display namespace information for the owning type</param>
        /// <param name="owningTypeOverride">Owning type override when non-null</param>
        /// <param name="signaturePrefix">Optional field name signature prefix</param>
        /// <returns>Textual representation of the field declaration</returns>
        private string EmitFieldDefinitionName(FieldDefinitionHandle fieldDefHandle, bool namespaceQualified, string owningTypeOverride, string signaturePrefix)
        {
            ValidateHandle(fieldDefHandle, TableIndex.Field);
            FieldDefinition fieldDef = _metadataReader.GetFieldDefinition(fieldDefHandle);
            DisassemblingGenericContext genericContext = new DisassemblingGenericContext(Array.Empty<string>(), Array.Empty<string>());
            StringBuilder output = new StringBuilder();
            output.Append(fieldDef.DecodeSignature<string, DisassemblingGenericContext>(this, genericContext));
            output.Append(' ');
            output.Append(EmitHandleName(fieldDef.GetDeclaringType(), namespaceQualified, owningTypeOverride));
            output.Append('.');
            output.Append(signaturePrefix);
            output.Append(_metadataReader.GetString(fieldDef.Name));
            return output.ToString();
        }

        private string EmitString(StringHandle handle)
        {
            return _metadataReader.GetString(handle);
        }
    }
