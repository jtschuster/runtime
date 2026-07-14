// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.DependencyAnalysisFramework;
using Mono.Cecil;
using Mono.Linker.Steps;

using CustomAttributeWithOrigin = (Mono.Cecil.CustomAttribute Attribute, Mono.Cecil.AssemblyDefinition Origin);

namespace Mono.Linker
{
    sealed class TypeMapDependencyGraph2
    {
        const string RootDependencyReason = "TypeMap root";
        const string StaticDependencyReason = "TypeMap dependency";
        const string ConditionalDependencyReason = "TypeMap condition";

        sealed class TypeMapMark(DependencyInfo reason, int order)
        {
            internal DependencyInfo Reason { get; } = reason;

            internal int Order { get; } = order;
        }

        struct TypeMapMarkStrategy : IDependencyAnalysisMarkStrategy<TypeMapNodeFactory>
        {
            int _nextMarkOrder;

            bool IDependencyAnalysisMarkStrategy<TypeMapNodeFactory>.MarkNode(
                DependencyNodeCore<TypeMapNodeFactory> node,
                DependencyNodeCore<TypeMapNodeFactory> reasonNode,
                DependencyNodeCore<TypeMapNodeFactory> reasonNode2,
                string reason)
            {
                if (node.Marked)
                    return false;

                DependencyInfo dependencyReason;
                if (reasonNode is null)
                {
                    dependencyReason = ((TypeMapDependencyNode)node).RootReason;
                }
                else if (reasonNode2 is null)
                {
                    dependencyReason = GetMark(reasonNode).Reason;
                }
                else
                {
                    TypeMapMark firstMark = GetMark(reasonNode);
                    TypeMapMark secondMark = GetMark(reasonNode2);
                    dependencyReason = firstMark.Order > secondMark.Order
                        ? firstMark.Reason
                        : secondMark.Reason;
                }

                node.SetMark(new TypeMapMark(dependencyReason, ++_nextMarkOrder));
                return true;
            }

            void IDependencyAnalysisMarkStrategy<TypeMapNodeFactory>.VisitLogNodes(
                IEnumerable<DependencyNodeCore<TypeMapNodeFactory>> nodeList,
                IDependencyAnalyzerLogNodeVisitor<TypeMapNodeFactory> logNodeVisitor)
            {
            }

            void IDependencyAnalysisMarkStrategy<TypeMapNodeFactory>.VisitLogEdges(
                IEnumerable<DependencyNodeCore<TypeMapNodeFactory>> nodeList,
                IDependencyAnalyzerLogEdgeVisitor<TypeMapNodeFactory> logEdgeVisitor)
            {
            }

            void IDependencyAnalysisMarkStrategy<TypeMapNodeFactory>.AttachContext(TypeMapNodeFactory context)
            {
            }

            static TypeMapMark GetMark(DependencyNodeCore<TypeMapNodeFactory> node) =>
                (TypeMapMark)node.GetMark();
        }

        abstract class TypeMapDependencyNode : DependencyNodeCore<TypeMapNodeFactory>
        {
            List<DependencyListEntry>? _staticDependencies;
            List<CombinedDependencyListEntry>? _conditionalDependencies;

            internal DependencyInfo RootReason { get; set; }

            public sealed override bool InterestingForDynamicDependencyAnalysis => false;

            public sealed override bool HasDynamicDependencies => false;

            public sealed override bool HasConditionalStaticDependencies => _conditionalDependencies is not null;

            public sealed override bool StaticDependenciesAreComputed => true;

            public sealed override IEnumerable<DependencyListEntry> GetStaticDependencies(TypeMapNodeFactory context) =>
                _staticDependencies is null ? [] : _staticDependencies;

            public sealed override IEnumerable<CombinedDependencyListEntry> GetConditionalStaticDependencies(TypeMapNodeFactory context) =>
                _conditionalDependencies is null ? [] : _conditionalDependencies;

            public sealed override IEnumerable<CombinedDependencyListEntry> SearchDynamicDependencies(
                List<DependencyNodeCore<TypeMapNodeFactory>> markedNodes,
                int firstNode,
                TypeMapNodeFactory context) => [];

            protected sealed override void OnMarked(TypeMapNodeFactory context) =>
                OnMarked(context.MarkStep, ((TypeMapMark)GetMark()).Reason);

            internal void AddStaticDependency(TypeMapDependencyNode target)
            {
                _staticDependencies ??= [];
                _staticDependencies.Add(new DependencyListEntry(target, StaticDependencyReason));
            }

            internal void AddConditionalDependency(TypeMapDependencyNode condition, TypeMapDependencyNode target)
            {
                _conditionalDependencies ??= [];
                _conditionalDependencies.Add(new CombinedDependencyListEntry(target, condition, ConditionalDependencyReason));
            }

            internal virtual void OnMarked(MarkStep markStep, DependencyInfo reason)
            {
            }
        }

        sealed class TypeMapUniverseNode(TypeReference group, string kind) : TypeMapDependencyNode
        {
            protected override string GetName(TypeMapNodeFactory context) => $"{kind} TypeMap universe: {group}";
        }

        sealed class TypeMapTrimTargetNode(TypeDefinition type, string kind) : TypeMapDependencyNode
        {
            protected override string GetName(TypeMapNodeFactory context) => $"{kind} TypeMap trim target: {type}";
        }

        sealed class TypeMapAttributeFoundNode(CustomAttributeWithOrigin entry) : TypeMapDependencyNode
        {
            protected override string GetName(TypeMapNodeFactory context) => $"TypeMap attribute found: {entry.Attribute}";
        }

        sealed class TypeMapConditionsPendingNode(CustomAttributeWithOrigin entry) : TypeMapDependencyNode
        {
            protected override string GetName(TypeMapNodeFactory context) => $"TypeMap attribute awaiting trim target: {entry.Attribute}";
        }

        sealed class TypeMapAttributeKeptNode(CustomAttributeWithOrigin entry) : TypeMapDependencyNode
        {
            internal override void OnMarked(MarkStep markStep, DependencyInfo reason) =>
                markStep.MarkCustomAttribute(entry.Attribute, reason, new MessageOrigin(entry.Origin));

            protected override string GetName(TypeMapNodeFactory context) => $"TypeMap attribute kept: {entry.Attribute}";
        }

        sealed class TypeMapTargetTypeNode(TypeDefinition type) : TypeMapDependencyNode
        {
            internal override void OnMarked(MarkStep markStep, DependencyInfo reason) =>
                markStep.MarkRequirementsForInstantiatedTypes(type);

            protected override string GetName(TypeMapNodeFactory context) => $"TypeMap target type: {type}";
        }

        sealed class TypeMapAssemblyContributionNode(AssemblyDefinition assembly) : TypeMapDependencyNode
        {
            internal override void OnMarked(MarkStep markStep, DependencyInfo reason) =>
                markStep.MarkAssembly(assembly, reason, new MessageOrigin(assembly));

            protected override string GetName(TypeMapNodeFactory context) => $"TypeMap assembly contribution: {assembly.Name.Name}";
        }

        sealed class TypeMapNodeFactory
        {
            readonly Dictionary<TypeReference, TypeMapUniverseNode> _externalUniverseNodes;
            readonly Dictionary<TypeReference, TypeMapUniverseNode> _proxyUniverseNodes;
            readonly Dictionary<TypeReference, TypeMapUniverseNode> _groupUniverseNodes;
            readonly Dictionary<TypeDefinition, TypeMapTrimTargetNode> _externalTrimTargetNodes = [];
            readonly Dictionary<TypeDefinition, TypeMapTrimTargetNode> _proxyTrimTargetNodes = [];
            readonly Dictionary<TypeDefinition, TypeMapTargetTypeNode> _targetTypeNodes = [];
            readonly Dictionary<AssemblyDefinition, TypeMapAssemblyContributionNode> _assemblyContributionNodes = [];

            internal TypeMapNodeFactory(MarkStep markStep, LinkContext context)
            {
                MarkStep = markStep;
                Context = context;
                var comparer = new TypeReferenceEqualityComparer(context);
                _externalUniverseNodes = new(comparer);
                _proxyUniverseNodes = new(comparer);
                _groupUniverseNodes = new(comparer);
            }

            internal MarkStep MarkStep { get; }

            internal LinkContext Context { get; }

            internal TypeMapUniverseNode GetExternalUniverseNode(TypeReference group) =>
                GetUniverseNode(_externalUniverseNodes, group, "External");

            internal TypeMapUniverseNode GetProxyUniverseNode(TypeReference group) =>
                GetUniverseNode(_proxyUniverseNodes, group, "Proxy");

            internal TypeMapUniverseNode GetGroupUniverseNode(TypeReference group) =>
                GetUniverseNode(_groupUniverseNodes, group, "Assembly target");

            internal TypeMapTrimTargetNode? TryGetExternalTrimTargetNode(TypeDefinition type) =>
                _externalTrimTargetNodes.GetValueOrDefault(type);

            internal TypeMapTrimTargetNode? TryGetProxyTrimTargetNode(TypeDefinition type) =>
                _proxyTrimTargetNodes.GetValueOrDefault(type);

            internal TypeMapTrimTargetNode GetExternalTrimTargetNode(TypeDefinition type) =>
                GetTrimTargetNode(_externalTrimTargetNodes, type, "External");

            internal TypeMapTrimTargetNode GetProxyTrimTargetNode(TypeDefinition type) =>
                GetTrimTargetNode(_proxyTrimTargetNodes, type, "Proxy");

            internal TypeMapTargetTypeNode GetTargetTypeNode(TypeDefinition type)
            {
                if (!_targetTypeNodes.TryGetValue(type, out TypeMapTargetTypeNode? node))
                {
                    node = new(type);
                    _targetTypeNodes.Add(type, node);
                }

                return node;
            }

            internal TypeMapAssemblyContributionNode GetAssemblyContributionNode(AssemblyDefinition assembly)
            {
                if (!_assemblyContributionNodes.TryGetValue(assembly, out TypeMapAssemblyContributionNode? node))
                {
                    node = new(assembly);
                    _assemblyContributionNodes.Add(assembly, node);
                }

                return node;
            }

            static TypeMapUniverseNode GetUniverseNode(
                Dictionary<TypeReference, TypeMapUniverseNode> nodes,
                TypeReference group,
                string kind)
            {
                if (!nodes.TryGetValue(group, out TypeMapUniverseNode? node))
                {
                    node = new(group, kind);
                    nodes.Add(group, node);
                }

                return node;
            }

            static TypeMapTrimTargetNode GetTrimTargetNode(
                Dictionary<TypeDefinition, TypeMapTrimTargetNode> nodes,
                TypeDefinition type,
                string kind)
            {
                if (!nodes.TryGetValue(type, out TypeMapTrimTargetNode? node))
                {
                    node = new(type, kind);
                    nodes.Add(type, node);
                }

                return node;
            }
        }

        readonly TypeMapNodeFactory _factory;
        readonly DependencyAnalyzer<TypeMapMarkStrategy, TypeMapNodeFactory> _dependencyGraph;
        bool _processingGraph;
        bool _scanning;

        internal TypeMapDependencyGraph2(LinkContext context, MarkStep markStep)
        {
            _factory = new(markStep, context);
            _dependencyGraph = new(_factory, resultSorter: null);
        }

        internal void ProcessExternalTypeMapGroupSeen(MethodDefinition callingMethod, TypeReference typeMapGroup)
        {
            AddRoot(
                _factory.GetExternalUniverseNode(typeMapGroup),
                new DependencyInfo(DependencyKind.TypeMapEntry, callingMethod));
            AddRoot(
                _factory.GetGroupUniverseNode(typeMapGroup),
                new DependencyInfo(DependencyKind.TypeMapAssemblyTarget, callingMethod));
        }

        internal void ProcessProxyTypeMapGroupSeen(MethodDefinition callingMethod, TypeReference typeMapGroup)
        {
            AddRoot(
                _factory.GetProxyUniverseNode(typeMapGroup),
                new DependencyInfo(DependencyKind.TypeMapEntry, callingMethod));
            AddRoot(
                _factory.GetGroupUniverseNode(typeMapGroup),
                new DependencyInfo(DependencyKind.TypeMapAssemblyTarget, callingMethod));
        }

        internal void ProcessRelevantType(TypeDefinition type)
        {
            if (_factory.TryGetExternalTrimTargetNode(type) is TypeMapTrimTargetNode node)
                AddRoot(node, new DependencyInfo(DependencyKind.TypeMapEntry, type));
        }

        internal void ProcessInstantiatedType(TypeDefinition type)
        {
            if (_factory.TryGetProxyTrimTargetNode(type) is TypeMapTrimTargetNode node)
                AddRoot(node, new DependencyInfo(DependencyKind.TypeMapEntry, type));
        }

        internal void ScanTypeMapAttributes(AssemblyDefinition? entryPointAssembly)
        {
            if (entryPointAssembly is null)
                return;

            _scanning = true;
            try
            {
                HashSet<AssemblyDefinition> seen = [entryPointAssembly];
                Queue<AssemblyDefinition> toVisit = new();
                toVisit.Enqueue(entryPointAssembly);
                while (toVisit.Count > 0)
                {
                    AssemblyDefinition assembly = toVisit.Dequeue();
                    foreach (CustomAttribute attribute in assembly.CustomAttributes)
                    {
                        if (attribute.AttributeType is not GenericInstanceType
                            {
                                Namespace: "System.Runtime.InteropServices",
                                GenericArguments: [TypeReference typeMapGroup]
                            })
                        {
                            continue;
                        }

                        CustomAttributeWithOrigin entry = (attribute, assembly);
                        if (attribute.AttributeType.Name is "TypeMapAttribute`1")
                        {
                            AddExternalTypeMapEntry(typeMapGroup, entry);
                        }
                        else if (attribute.AttributeType.Name is "TypeMapAssociationAttribute`1")
                        {
                            AddProxyTypeMapEntry(typeMapGroup, entry);
                        }
                        else if (attribute.AttributeType.Name is "TypeMapAssemblyTargetAttribute`1"
                            && attribute.ConstructorArguments is [{ Value: string assemblyName }])
                        {
                            AssemblyNameReference targetAssemblyName = AssemblyNameReference.Parse(assemblyName);
                            if (_factory.Context.TryResolve(targetAssemblyName) is AssemblyDefinition targetAssembly)
                            {
                                AddAssemblyTarget(typeMapGroup, entry, targetAssembly);
                                if (seen.Add(targetAssembly))
                                    toVisit.Enqueue(targetAssembly);
                            }
                        }
                    }
                }
            }
            finally
            {
                _scanning = false;
            }

            ProcessGraph();
        }

        void AddExternalTypeMapEntry(TypeReference group, CustomAttributeWithOrigin entry)
        {
            if (entry.Attribute.ConstructorArguments is [_, { Value: TypeReference targetType }, { Value: TypeReference trimTarget }])
            {
                AddTypeMapEntry(
                    entry,
                    _factory.GetExternalUniverseNode(group),
                    targetType,
                    UnwrapToResolvableType(trimTarget),
                    isProxy: false);
            }
            else if (entry.Attribute.ConstructorArguments is [_, { Value: TypeReference unconditionalTargetType }])
            {
                AddTypeMapEntry(
                    entry,
                    _factory.GetExternalUniverseNode(group),
                    unconditionalTargetType,
                    dependencySource: null,
                    isProxy: false);
            }
        }

        void AddProxyTypeMapEntry(TypeReference group, CustomAttributeWithOrigin entry)
        {
            if (entry.Attribute.ConstructorArguments is [{ Value: TypeReference sourceType }, { Value: TypeReference targetType }])
            {
                AddTypeMapEntry(
                    entry,
                    _factory.GetProxyUniverseNode(group),
                    targetType,
                    UnwrapToResolvableType(sourceType),
                    isProxy: true);
            }
        }

        void AddTypeMapEntry(
            CustomAttributeWithOrigin entry,
            TypeMapUniverseNode universeNode,
            TypeReference targetType,
            TypeReference? dependencySource,
            bool isProxy)
        {
            TypeMapAttributeFoundNode foundNode = new(entry);
            TypeMapAttributeKeptNode keptNode = new(entry);
            keptNode.AddStaticDependency(_factory.GetAssemblyContributionNode(entry.Origin));
            if (_factory.Context.Resolve(UnwrapToResolvableType(targetType)) is TypeDefinition targetTypeDefinition)
                keptNode.AddStaticDependency(_factory.GetTargetTypeNode(targetTypeDefinition));

            if (dependencySource is null)
            {
                foundNode.AddConditionalDependency(universeNode, keptNode);
            }
            else if (_factory.Context.Resolve(dependencySource) is TypeDefinition dependencyType)
            {
                TypeMapTrimTargetNode trimTargetNode = isProxy
                    ? _factory.GetProxyTrimTargetNode(dependencyType)
                    : _factory.GetExternalTrimTargetNode(dependencyType);
                TypeMapConditionsPendingNode pendingNode = new(entry);
                foundNode.AddConditionalDependency(universeNode, pendingNode);
                pendingNode.AddConditionalDependency(trimTargetNode, keptNode);

                if (_factory.Context.Annotations.IsMarked(dependencyType))
                {
                    AddRoot(
                        trimTargetNode,
                        new DependencyInfo(DependencyKind.TypeMapEntry, dependencySource));
                }
            }

            AddRoot(
                foundNode,
                new DependencyInfo(DependencyKind.TypeMapEntry, entry.Attribute));
        }

        void AddAssemblyTarget(
            TypeReference group,
            CustomAttributeWithOrigin entry,
            AssemblyDefinition targetAssembly)
        {
            TypeMapAttributeFoundNode foundNode = new(entry);
            TypeMapConditionsPendingNode pendingNode = new(entry);
            TypeMapAttributeKeptNode keptNode = new(entry);
            keptNode.AddStaticDependency(_factory.GetAssemblyContributionNode(entry.Origin));

            foundNode.AddConditionalDependency(_factory.GetGroupUniverseNode(group), pendingNode);
            pendingNode.AddConditionalDependency(_factory.GetAssemblyContributionNode(targetAssembly), keptNode);
            AddRoot(
                foundNode,
                new DependencyInfo(DependencyKind.TypeMapAssemblyTarget, entry.Attribute));
        }

        void AddRoot(TypeMapDependencyNode node, DependencyInfo reason)
        {
            if (node.Marked)
                return;

            node.RootReason = reason;
            _dependencyGraph.AddRoot(node, RootDependencyReason);
            if (!_scanning)
                ProcessGraph();
        }

        void ProcessGraph()
        {
            if (_processingGraph)
                return;

            _processingGraph = true;
            try
            {
                _dependencyGraph.ComputeMarkedNodesIncrementally();
            }
            finally
            {
                _processingGraph = false;
            }
        }

        static TypeReference UnwrapToResolvableType(TypeReference type)
        {
            while (type is TypeSpecification { ElementType: var elementType } and not GenericInstanceType)
                type = elementType;
            return type;
        }
    }
}
