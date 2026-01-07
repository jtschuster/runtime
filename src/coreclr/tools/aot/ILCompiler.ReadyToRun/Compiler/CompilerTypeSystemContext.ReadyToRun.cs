// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Internal.TypeSystem;
using Internal.IL;

using Interlocked = System.Threading.Interlocked;
using Internal.TypeSystem.Ecma;

namespace ILCompiler
{
    public partial class CompilerTypeSystemContext
    {
        public CompilerTypeSystemContext()
        {
            _continuationTypeHashtable = new ContinuationTypeHashtable(this);
        }

        public MetadataType GetContinuationType(GCPointerMap pointerMap, EcmaModule owningModule)
        {
            var cont = _continuationTypeHashtable.GetOrCreateValue(new (pointerMap, owningModule));
            _validTypes.TryAdd(cont);
            return cont;
        }

        private readonly struct ContinuationTypeHashtableKey : IEquatable<ContinuationTypeHashtableKey>
        {
            public GCPointerMap PointerMap { get; }
            public EcmaModule OwningModule { get; }
            public ContinuationTypeHashtableKey(GCPointerMap pointerMap, EcmaModule owningModule)
            {
                PointerMap = pointerMap;
                OwningModule = owningModule;
            }
            public bool Equals(ContinuationTypeHashtableKey other)
            {
                return PointerMap.Equals(other.PointerMap) && OwningModule.Equals(other.OwningModule);
            }
            public override int GetHashCode()
            {
                return HashCode.Combine(PointerMap.GetHashCode(), OwningModule.GetHashCode());
            }
        }

        private sealed class ContinuationTypeHashtable : LockFreeReaderHashtable<ContinuationTypeHashtableKey, AsyncContinuationType>
        {
            private readonly CompilerTypeSystemContext _parent;

            public ContinuationTypeHashtable(CompilerTypeSystemContext parent)
                => _parent = parent;

            protected override int GetKeyHashCode(ContinuationTypeHashtableKey key) => HashCode.Combine(key.PointerMap.GetHashCode(), key.OwningModule.GetHashCode());
            protected override int GetValueHashCode(AsyncContinuationType value) => value.PointerMap.GetHashCode();
            protected override bool CompareKeyToValue(ContinuationTypeHashtableKey key, AsyncContinuationType value) => key.PointerMap.Equals(value.PointerMap) && key.OwningModule.Equals(value);
            protected override bool CompareValueToValue(AsyncContinuationType value1, AsyncContinuationType value2)
                => value1.PointerMap.Equals(value2.PointerMap);
            protected override AsyncContinuationType CreateValueFromKey(ContinuationTypeHashtableKey key)
            {
                return new AsyncContinuationType(_parent.ContinuationType, key.PointerMap, key.OwningModule);
            }
        }
    }
}
