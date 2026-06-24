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

        /// <summary>
        /// For a shared (canonical) instance method on a generic value type, gets a compilable thunk
        /// that can be called given a boxed version of the generic value type as its 'this' pointer.
        /// The thunk is a method on a generic boxed-layout reference type (BoxedValueType), so the
        /// JIT sees a boxed-object receiver with the correct calling convention; the body loads the
        /// hidden generic context from the boxed object's MethodTable and dispatches to
        /// <paramref name="targetMethod"/>. Mirrors NativeAOT's GetSpecialUnboxingThunk.
        /// </summary>
        public MethodDesc GetGenericUnboxingThunk(MethodDesc targetMethod)
        {
            Debug.Assert(targetMethod.IsSharedByGenericInstantiations);
            Debug.Assert(!targetMethod.Signature.IsStatic);
            Debug.Assert(!targetMethod.HasInstantiation);

            TypeDesc owningType = targetMethod.OwningType;
            Debug.Assert(owningType.IsValueType);

            var owningTypeDefinition = (MetadataType)owningType.GetTypeDefinition();

            // Get a reference type that has the same layout as the boxed value type.
            BoxedValueType boxedTypeDefinition = _boxedValueTypeHashtable.GetOrCreateValue(owningTypeDefinition);

            // Get a method on the reference type with the same signature as the target method (but a
            // different calling convention, since 'this' will be a reference type).
            var targetMethodDefinition = targetMethod.GetTypicalMethodDefinition();
            GenericUnboxingThunk thunkDefinition = _genericUnboxingThunkHashtable.GetOrCreateValue(
                new GenericUnboxingThunkKey(targetMethodDefinition, boxedTypeDefinition));

            // Find the thunk on the instantiated version of the reference type.
            Debug.Assert(owningType != owningTypeDefinition);
            InstantiatedType boxedType = boxedTypeDefinition.MakeInstantiatedType(owningType.Instantiation);

            MethodDesc thunk = GetMethodForInstantiatedType(thunkDefinition, boxedType);
            Debug.Assert(!thunk.HasInstantiation);

            return thunk;
        }

        /// <summary>
        /// Does a method represent a (shared-generic) special unboxing thunk?
        /// </summary>
        public bool IsSpecialUnboxingThunk(MethodDesc method)
        {
            return method.GetTypicalMethodDefinition().GetType() == typeof(GenericUnboxingThunk);
        }

        /// <summary>
        /// Convert from a special unboxing thunk to the actual target method it dispatches to.
        /// </summary>
        public MethodDesc GetTargetOfSpecialUnboxingThunk(MethodDesc method)
        {
            MethodDesc typicalMethodTarget = ((GenericUnboxingThunk)method.GetTypicalMethodDefinition()).TargetMethod;

            MethodDesc methodOnInstantiatedType = typicalMethodTarget;
            if (method.OwningType.HasInstantiation)
            {
                InstantiatedType instantiatedType = GetInstantiatedType((MetadataType)typicalMethodTarget.OwningType, method.OwningType.Instantiation);
                methodOnInstantiatedType = GetMethodForInstantiatedType(typicalMethodTarget, instantiatedType);
            }

            MethodDesc instantiatedMethod = methodOnInstantiatedType;
            if (method.HasInstantiation)
            {
                instantiatedMethod = GetInstantiatedMethod(methodOnInstantiatedType, method.Instantiation);
            }

            return instantiatedMethod;
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

        private struct GenericUnboxingThunkKey
        {
            public readonly MethodDesc TargetMethod;
            public readonly BoxedValueType OwningType;

            public GenericUnboxingThunkKey(MethodDesc targetMethod, BoxedValueType owningType)
            {
                TargetMethod = targetMethod;
                OwningType = owningType;
            }
        }

        private sealed class GenericUnboxingThunkHashtable : LockFreeReaderHashtable<GenericUnboxingThunkKey, GenericUnboxingThunk>
        {
            protected override int GetKeyHashCode(GenericUnboxingThunkKey key) => key.TargetMethod.GetHashCode();
            protected override int GetValueHashCode(GenericUnboxingThunk value) => value.TargetMethod.GetHashCode();
            protected override bool CompareKeyToValue(GenericUnboxingThunkKey key, GenericUnboxingThunk value) => ReferenceEquals(key.TargetMethod, value.TargetMethod) && ReferenceEquals(key.OwningType, value.OwningType);
            protected override bool CompareValueToValue(GenericUnboxingThunk value1, GenericUnboxingThunk value2) => ReferenceEquals(value1.TargetMethod, value2.TargetMethod) && ReferenceEquals(value1.OwningType, value2.OwningType);
            protected override GenericUnboxingThunk CreateValueFromKey(GenericUnboxingThunkKey key) => new GenericUnboxingThunk(key.OwningType, key.TargetMethod);
        }
        private GenericUnboxingThunkHashtable _genericUnboxingThunkHashtable = new GenericUnboxingThunkHashtable();
    }
}
