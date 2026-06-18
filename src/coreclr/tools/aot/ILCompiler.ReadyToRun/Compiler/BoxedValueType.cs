// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Internal;
using Internal.Text;
using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    // A reference type whose instance layout is identical to the layout of a boxed
    // value type (an object header followed by the value type's data). The R2R unboxing
    // stub is modeled as an instance method on this type so that the stub body's calling
    // convention matches the convention the caller uses to invoke the unboxing stub
    // (i.e. 'this' is a boxed object reference, not a byref to the value).
    //
    // This mirrors the BoxedValueType used by the NativeAOT compiler
    // (CompilerTypeSystemContext.BoxedTypes.cs). The R2R variant is intentionally
    // restricted to non-generic value types for now; generic value types (which also
    // require a generic context to be threaded through) are handled separately.
    internal sealed class BoxedValueType : MetadataType, INonEmittableType, IPrefixMangledType
    {
        public MetadataType ValueTypeRepresented { get; }

        public override ModuleDesc Module { get; }

        public override Utf8Span Name => "Boxed_"u8.Append(ValueTypeRepresented.Name);
        public override Utf8Span Namespace => ValueTypeRepresented.Namespace;
        public override string DiagnosticName => "Boxed_" + ValueTypeRepresented.DiagnosticName;
        public override string DiagnosticNamespace => ValueTypeRepresented.DiagnosticNamespace;
        public override Instantiation Instantiation => ValueTypeRepresented.Instantiation;
        public override PInvokeStringFormat PInvokeStringFormat => PInvokeStringFormat.AutoClass;
        public override bool IsExplicitLayout => false;
        public override bool IsSequentialLayout => true;
        public override bool IsExtendedLayout => false;
        public override bool IsAutoLayout => false;
        public override bool IsBeforeFieldInit => false;
        public override MetadataType BaseType => (MetadataType)Context.GetWellKnownType(WellKnownType.Object);
        public override bool IsSealed => true;
        public override bool IsAbstract => false;
        public override MetadataType ContainingType => null;
        public override DefType[] ExplicitlyImplementedInterfaces => Array.Empty<DefType>();
        public override TypeSystemContext Context => ValueTypeRepresented.Context;

        public BoxedValueType(ModuleDesc owningModule, MetadataType valuetype)
        {
            Debug.Assert(valuetype.IsTypeDefinition);
            Debug.Assert(valuetype.IsValueType);

            Module = owningModule;
            ValueTypeRepresented = valuetype;
        }

        public override ClassLayoutMetadata GetClassLayout() => default(ClassLayoutMetadata);
        public override bool HasCustomAttribute(string attributeNamespace, string attributeName) => false;
        public override IEnumerable<MetadataType> GetNestedTypes() => Array.Empty<MetadataType>();
        public override MetadataType GetNestedType(Utf8Span name) => null;
        protected override MethodImplRecord[] ComputeVirtualMethodImplsForType() => Array.Empty<MethodImplRecord>();
        public override MethodImplRecord[] FindMethodsImplWithMatchingDeclName(Utf8Span name) => Array.Empty<MethodImplRecord>();

        public override int GetHashCode() => VersionResilientHashCode.NameHashCode(Namespace, Name);

        protected override TypeFlags ComputeTypeFlags(TypeFlags mask)
        {
            TypeFlags flags = 0;

            if ((mask & TypeFlags.HasGenericVarianceComputed) != 0)
            {
                flags |= TypeFlags.HasGenericVarianceComputed;
            }

            if ((mask & TypeFlags.CategoryMask) != 0)
            {
                flags |= TypeFlags.Class;
            }

            flags |= TypeFlags.HasFinalizerComputed;
            flags |= TypeFlags.AttributeCacheComputed;

            return flags;
        }

        public override FieldDesc GetField(Utf8Span name) => null;

        public override IEnumerable<FieldDesc> GetFields() => Array.Empty<FieldDesc>();

        // IPrefixMangledType: the mangled name is derived from the represented value type
        // with a "Boxed" prefix, matching the NativeAOT scheme.
        TypeDesc IPrefixMangledType.BaseType => ValueTypeRepresented;
        ReadOnlySpan<byte> IPrefixMangledType.Prefix => "Boxed"u8;

        // Deterministic ordering support.
        protected override int ClassCode => 1062019524;

        protected override int CompareToImpl(TypeDesc other, TypeSystemComparer comparer)
        {
            return comparer.Compare(ValueTypeRepresented, ((BoxedValueType)other).ValueTypeRepresented);
        }
    }
}
