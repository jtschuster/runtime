// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ILCompiler.DependencyAnalysisFramework;
using ILLink.Shared;
using Mono.Cecil;

namespace Mono.Linker.Steps
{

	public partial class MarkStep
	{
		internal sealed class TypeDefinitionNode : DependencyNodeCore<NodeFactory>, ILegacyTracingNode
		{
			readonly TypeDefinition type;

			public TypeDefinitionNode (TypeDefinition type)
			{
				this.type = type;
			}

			public override bool InterestingForDynamicDependencyAnalysis => false;

			public override bool HasDynamicDependencies => false;

			public override bool HasConditionalStaticDependencies => false;

			public override bool StaticDependenciesAreComputed => true;

			public override IEnumerable<DependencyListEntry>? GetStaticDependencies (NodeFactory context)
			{
				var MarkStep = context.MarkStep;
				var ScopeStack = context.MarkStep.ScopeStack;
				var Context = context.MarkStep.Context;

				using var typeScope = ScopeStack.PushLocalScope (new MessageOrigin (type));

				foreach (Action<TypeDefinition> handleMarkType in MarkStep.MarkContext.MarkTypeActions)
					handleMarkType (type);

				var baseDefinition = MarkStep.PreprocessMarkedType(type.BaseType, new DependencyInfo (DependencyKind.BaseType, type), ScopeStack.CurrentScope.Origin);
				if (baseDefinition != null) {
					yield return new (context.GetTypeNode (baseDefinition), Enum.GetName(DependencyKind.DeclaringType));
				}

				// The DynamicallyAccessedMembers hierarchy processing must be done after the base type was marked
				// (to avoid inconsistencies in the cache), but before anything else as work done below
				// might need the results of the processing here.
				MarkStep.DynamicallyAccessedMembersTypeHierarchy.ProcessMarkedTypeForDynamicallyAccessedMembersHierarchy (type);

				if (type.DeclaringType != null) {
					var declaringDefinition = MarkStep.PreprocessMarkedType (type.DeclaringType, new DependencyInfo (DependencyKind.DeclaringType, type), ScopeStack.CurrentScope.Origin);
					Debug.Assert (declaringDefinition is not null);
					yield return new (context.GetTypeNode (declaringDefinition), Enum.GetName(DependencyKind.DeclaringType));
				}

				MarkStep.MarkCustomAttributes (type, new DependencyInfo (DependencyKind.CustomAttribute, type));
				MarkStep.MarkSecurityDeclarations (type, new DependencyInfo (DependencyKind.CustomAttribute, type));

				if (Context.TryResolve (type.BaseType) is TypeDefinition baseType &&
					!MarkStep.Annotations.HasLinkerAttribute<RequiresUnreferencedCodeAttribute> (type) &&
					MarkStep.Annotations.TryGetLinkerAttribute (baseType, out RequiresUnreferencedCodeAttribute? effectiveRequiresUnreferencedCode)) {

					var currentOrigin = ScopeStack.CurrentScope.Origin;

					string arg1 = MessageFormat.FormatRequiresAttributeMessageArg (effectiveRequiresUnreferencedCode.Message);
					string arg2 = MessageFormat.FormatRequiresAttributeUrlArg (effectiveRequiresUnreferencedCode.Url);
					Context.LogWarning (currentOrigin, DiagnosticId.RequiresUnreferencedCodeOnBaseClass, type.GetDisplayName (), type.BaseType.GetDisplayName (), arg1, arg2);
				}


				if (type.IsMulticastDelegate ()) {
					MarkStep.MarkMulticastDelegate (type);
				}

				if (type.IsClass && type.BaseType == null && type.Name == "Object" && MarkStep.ShouldMarkSystemObjectFinalize)
					MarkStep.MarkMethodIf (type.Methods, static m => m.Name == "Finalize", new DependencyInfo (DependencyKind.MethodForSpecialType, type), ScopeStack.CurrentScope.Origin);

				MarkStep.MarkSerializable (type);

				// This marks static fields of KeyWords/OpCodes/Tasks subclasses of an EventSource type.
				// The special handling of EventSource is still needed in .NET6 in library mode
				if ((!Context.DisableEventSourceSpecialHandling || Context.GetTargetRuntimeVersion () < TargetRuntimeVersion.NET6) && BCL.EventTracingForWindows.IsEventSourceImplementation (type, Context)) {
					MarkStep.MarkEventSourceProviders (type);
				}

				// This marks properties for [EventData] types as well as other attribute dependencies.
				MarkStep.MarkTypeSpecialCustomAttributes (type);

				MarkStep.MarkGenericParameterProvider (type);

				// There are a number of markings we can defer until later when we know it's possible a reference type could be instantiated
				// For example, if no instance of a type exist, then we don't need to mark the interfaces on that type -- Note this is not true for static interfaces
				// However, for some other types there is no benefit to deferring
				if (type.IsInterface) {
					// There's no benefit to deferring processing of an interface type until we know a type implementing that interface is marked
					MarkStep.MarkRequirementsForInstantiatedTypes (type);
				} else if (type.IsValueType) {
					// Note : Technically interfaces could be removed from value types in some of the same cases as reference types, however, it's harder to know when
					// a value type instance could exist.  You'd have to track initobj and maybe locals types.  Going to punt for now.
					MarkStep.MarkRequirementsForInstantiatedTypes (type);
				} else if (MarkStep.IsFullyPreserved (type)) {
					// Here for a couple reasons:
					// * Edge case to cover a scenario where a type has preserve all, implements interfaces, but does not have any instance ctors.
					//    Normally TypePreserve.All would cause an instance ctor to be marked and that would in turn lead to MarkInterfaceImplementations being called
					//    Without an instance ctor, MarkInterfaceImplementations is not called and then TypePreserve.All isn't truly respected.
					// * If an assembly has the action Copy and had ResolveFromAssemblyStep ran for the assembly, then InitializeType will have led us here
					//    When the entire assembly is preserved, then all interfaces, base, etc will be preserved on the type, so we need to make sure
					//    all of these types are marked.  For example, if an interface implementation is of a type in another assembly that is linked,
					//    and there are no other usages of that interface type, then we need to make sure the interface type is still marked because
					//    this type is going to retain the interface implementation
					MarkStep.MarkRequirementsForInstantiatedTypes (type);
				} else if (MarkStep.AlwaysMarkTypeAsInstantiated (type)) {
					MarkStep.MarkRequirementsForInstantiatedTypes (type);
				}

				// Save for later once we know which interfaces are marked and then determine which interface implementations and methods to keep
				if (type.HasInterfaces)
					MarkStep._typesWithInterfaces.Add ((type, ScopeStack.CurrentScope));

				if (type.HasMethods) {
					// TODO: MarkMethodIfNeededByBaseMethod should include logic for IsMethodNeededByTypeDueToPreservedScope: https://github.com/dotnet/linker/issues/3090
					foreach (var method in type.Methods) {
						MarkStep.MarkMethodIfNeededByBaseMethod (method);
						if (MarkStep.IsMethodNeededByTypeDueToPreservedScope (method)) {
							// For methods that must be preserved, blame the declaring type.
							MarkStep.MarkMethod (method, new DependencyInfo (DependencyKind.VirtualNeededDueToPreservedScope, type), ScopeStack.CurrentScope.Origin);
						}
					}
				}

				MarkStep.DoAdditionalTypeProcessing (type);

				MarkStep.ApplyPreserveInfo (type);
				MarkStep.ApplyPreserveMethods (type);

				yield break;
			}

			public override IEnumerable<CombinedDependencyListEntry>? GetConditionalStaticDependencies (NodeFactory context) => null;

			public override IEnumerable<CombinedDependencyListEntry>? SearchDynamicDependencies (List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory context) => null;

			protected override string GetName (NodeFactory context) => type.GetDisplayName ();

			object ILegacyTracingNode.DependencyObject => type;
		}
	}
}
