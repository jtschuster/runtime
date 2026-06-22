// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    // R2R-specific functionality for compiling unboxing stubs into the image.
    // (NativeAOT has an equivalent in CompilerTypeSystemContext.BoxedTypes.cs.)
    public partial class CompilerTypeSystemContext
    {
        /// <summary>
        /// Gets a compilable method that represents the unboxing entrypoint of an instance
        /// method on a value type. The returned method, when given a boxed value type as its
        /// 'this' pointer, extracts a byref to the value and dispatches to <paramref name="targetMethod"/>.
        /// Restricted to non-generic value types for now.
        /// </summary>
        public MethodDesc GetUnboxingThunk(MethodDesc targetMethod)
        {
            Debug.Assert(targetMethod.OwningType.IsValueType);
            Debug.Assert(!targetMethod.Signature.IsStatic);
            Debug.Assert(!targetMethod.HasInstantiation);
            Debug.Assert(!targetMethod.OwningType.HasInstantiation);

            var valueType = (MetadataType)targetMethod.OwningType;
            BoxedValueType boxedType = _boxedValueTypeHashtable.GetOrCreateValue(valueType);
            return _unboxingStubHashtable.GetOrCreateValue(new UnboxingStubKey(targetMethod, boxedType));
        }

        private sealed class BoxedValueTypeHashtable : LockFreeReaderHashtable<MetadataType, BoxedValueType>
        {
            protected override int GetKeyHashCode(MetadataType key) => key.GetHashCode();
            protected override int GetValueHashCode(BoxedValueType value) => value.ValueTypeRepresented.GetHashCode();
            protected override bool CompareKeyToValue(MetadataType key, BoxedValueType value) => ReferenceEquals(key, value.ValueTypeRepresented);
            protected override bool CompareValueToValue(BoxedValueType value1, BoxedValueType value2) => ReferenceEquals(value1.ValueTypeRepresented, value2.ValueTypeRepresented);
            protected override BoxedValueType CreateValueFromKey(MetadataType key) => new BoxedValueType(key.Module, key);
        }
        private BoxedValueTypeHashtable _boxedValueTypeHashtable = new BoxedValueTypeHashtable();

        private struct UnboxingStubKey
        {
            public readonly MethodDesc TargetMethod;
            public readonly BoxedValueType OwningType;

            public UnboxingStubKey(MethodDesc targetMethod, BoxedValueType owningType)
            {
                TargetMethod = targetMethod;
                OwningType = owningType;
            }
        }

        private sealed class UnboxingStubHashtable : LockFreeReaderHashtable<UnboxingStubKey, UnboxingStubMethod>
        {
            protected override int GetKeyHashCode(UnboxingStubKey key) => key.TargetMethod.GetHashCode();
            protected override int GetValueHashCode(UnboxingStubMethod value) => value.TargetMethod.GetHashCode();
            // NOTE: UnboxingStubMethod.OwningType now delegates to the real value type (it is a
            // MethodDelegator), so the boxed-layout owner is compared via BoxedThisType, not OwningType.
            protected override bool CompareKeyToValue(UnboxingStubKey key, UnboxingStubMethod value) => ReferenceEquals(key.TargetMethod, value.TargetMethod) && ReferenceEquals(key.OwningType, value.BoxedThisType);
            protected override bool CompareValueToValue(UnboxingStubMethod value1, UnboxingStubMethod value2) => ReferenceEquals(value1.TargetMethod, value2.TargetMethod) && ReferenceEquals(value1.BoxedThisType, value2.BoxedThisType);
            protected override UnboxingStubMethod CreateValueFromKey(UnboxingStubKey key) => new UnboxingStubMethod(key.OwningType, key.TargetMethod);
        }
        private UnboxingStubHashtable _unboxingStubHashtable = new UnboxingStubHashtable();
    }
}
