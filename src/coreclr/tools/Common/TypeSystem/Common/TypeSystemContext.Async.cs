// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using Internal.NativeFormat;
using Internal.TypeSystem.Ecma;

#if TYPE_LOADER_IMPLEMENTATION
using MetadataType = Internal.TypeSystem.DefType;
#endif

namespace Internal.TypeSystem
{
    public abstract partial class TypeSystemContext
    {
        public MethodDesc GetAsyncVariantMethod(MethodDesc taskReturningMethod)
        {
            Debug.Assert(taskReturningMethod.Signature.ReturnsTaskOrValueTask());
            MethodDesc asyncMetadataMethodDef = taskReturningMethod.GetTypicalMethodDefinition();
            MethodDesc result = _asyncVariantImplHashtable.GetOrCreateValue((EcmaMethod)asyncMetadataMethodDef);

            if (asyncMetadataMethodDef != taskReturningMethod)
            {
                TypeDesc owningType = taskReturningMethod.OwningType;
                if (owningType.HasInstantiation)
                    result = GetMethodForInstantiatedType(result, (InstantiatedType)owningType);

                if (taskReturningMethod.HasInstantiation)
                    result = GetInstantiatedMethod(result, taskReturningMethod.Instantiation);
            }

            return result;
        }

        private sealed class AsyncVariantImplHashtable : LockFreeReaderHashtable<EcmaMethod, AsyncMethodVariant>
        {
            protected override int GetKeyHashCode(EcmaMethod key) => key.GetHashCode();
            protected override int GetValueHashCode(AsyncMethodVariant value) => value.Target.GetHashCode();
            protected override bool CompareKeyToValue(EcmaMethod key, AsyncMethodVariant value) => key == value.Target;
            protected override bool CompareValueToValue(AsyncMethodVariant value1, AsyncMethodVariant value2)
                => value1.Target == value2.Target;
            protected override AsyncMethodVariant CreateValueFromKey(EcmaMethod key) => new AsyncMethodVariant(key);
        }
        private AsyncVariantImplHashtable _asyncVariantImplHashtable = new AsyncVariantImplHashtable();
    }
}
