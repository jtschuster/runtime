// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using Mono.Cecil;
using Mono.Linker.Steps;

namespace Mono.Linker
{
    sealed class TypeMapHandler
    {
        TypeMapDependencyGraph2? _dependencyGraph;

        public TypeMapHandler()
        {
        }

        [Conditional("DEBUG")]
        private void EnsureInitialized()
        {
            if (_dependencyGraph is null)
                throw new InvalidOperationException("TypeMapHandler not initialized");
        }

        public void Initialize(LinkContext context, MarkStep markStep, AssemblyDefinition? entryPointAssembly)
        {
            _dependencyGraph = new(context, markStep);
            _dependencyGraph.ScanTypeMapAttributes(entryPointAssembly);
        }

        public void ProcessExternalTypeMapGroupSeen(MethodDefinition callingMethod, TypeReference typeMapGroup)
        {
            EnsureInitialized();
            _dependencyGraph!.ProcessExternalTypeMapGroupSeen(callingMethod, typeMapGroup);
        }

        public void ProcessProxyTypeMapGroupSeen(MethodDefinition callingMethod, TypeReference typeMapGroup)
        {
            EnsureInitialized();
            _dependencyGraph!.ProcessProxyTypeMapGroupSeen(callingMethod, typeMapGroup);
        }

        public void ProcessType(TypeDefinition definition)
        {
            EnsureInitialized();
            _dependencyGraph!.ProcessRelevantType(definition);
        }

        public void ProcessInstantiated(TypeDefinition definition)
        {
            EnsureInitialized();
            _dependencyGraph!.ProcessInstantiatedType(definition);
        }

        public static bool IsTypeMapAttributeType(TypeDefinition type)
        {
            return type is { Namespace: "System.Runtime.InteropServices", Name: "TypeMapAttribute`1" or "TypeMapAssociationAttribute`1" or "TypeMapAssemblyTargetAttribute`1" };
        }
    }
}
