// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ILCompiler.DependencyAnalysisFramework;
using ILLink.Shared;
using Mono.Cecil;

namespace Mono.Linker.Steps
{
	public partial class MarkStep
	{
		internal sealed class MethodDefinitionNode : DependencyNodeCore<NodeFactory>, ILegacyTracingNode
		{
			readonly MethodDefinition method;
			readonly DependencyInfo reason;

			public MethodDefinitionNode (MethodDefinition method, DependencyInfo reason)
			{
				this.method = method;
				this.reason = reason;
			}

			public override bool InterestingForDynamicDependencyAnalysis => false;

			public override bool HasDynamicDependencies => false;

			public override bool HasConditionalStaticDependencies => false;

			public override bool StaticDependenciesAreComputed => true;

			public override IEnumerable<DependencyListEntry>? GetStaticDependencies (NodeFactory context)
			{
				var markStep = context.MarkStep;
				var ScopeStack = context.MarkStep.ScopeStack;
				var MarkContext = context.MarkStep.MarkContext;

				ScopeStack.AssertIsEmpty ();
				using var methodScope = ScopeStack.PushLocalScope (new MessageOrigin (method));

				bool markedForCall =
					reason.Kind == DependencyKind.DirectCall ||
					reason.Kind == DependencyKind.VirtualCall ||
					reason.Kind == DependencyKind.Newobj;

				foreach (Action<MethodDefinition> handleMarkMethod in MarkContext.MarkMethodActions)
					handleMarkMethod (method);

				if (!markedForCall) {

					markStep.PreprocessMarkedType (method.DeclaringType, new DependencyInfo (DependencyKind.DeclaringType, method), ScopeStack.CurrentScope.Origin);
					yield return new (context.GetTypeNode (method.DeclaringType), nameof(DependencyKind.DeclaringType));
					//markStep.MarkType (method.DeclaringType, new DependencyInfo (DependencyKind.DeclaringType, method));
				}

				markStep.MarkCustomAttributes (method, new DependencyInfo (DependencyKind.CustomAttribute, method));
				markStep.MarkSecurityDeclarations (method, new DependencyInfo (DependencyKind.CustomAttribute, method));

				markStep.MarkGenericParameterProvider (method);

				if (method.IsInstanceConstructor ()) {
					markStep.MarkRequirementsForInstantiatedTypes (method.DeclaringType);
					markStep.Tracer.AddDirectDependency (method.DeclaringType, new DependencyInfo (DependencyKind.InstantiatedByCtor, method), marked: false);
				} else if (method.IsStaticConstructor () && markStep.Annotations.HasLinkerAttribute<RequiresUnreferencedCodeAttribute> (method))
					markStep.Context.LogWarning (ScopeStack.CurrentScope.Origin, DiagnosticId.RequiresUnreferencedCodeOnStaticConstructor, method.GetDisplayName ());

				if (method.IsConstructor) {
					if (!markStep.Annotations.ProcessSatelliteAssemblies && KnownMembers.IsSatelliteAssemblyMarker (method))
						markStep.Annotations.ProcessSatelliteAssemblies = true;
				} else if (method.TryGetProperty (out PropertyDefinition? property))
					markStep.MarkProperty (property, new DependencyInfo (PropagateDependencyKindToAccessors (reason.Kind, DependencyKind.PropertyOfPropertyMethod), method));
				else if (method.TryGetEvent (out EventDefinition? @event)) {
					markStep.MarkEvent (@event, new DependencyInfo (PropagateDependencyKindToAccessors (reason.Kind, DependencyKind.EventOfEventMethod), method));
				}

				if (method.HasMetadataParameters ()) {
#pragma warning disable RS0030 // MethodReference.Parameters is banned. It's easiest to leave the code as is for now
					foreach (ParameterDefinition pd in method.Parameters) {
						markStep.MarkType (pd.ParameterType, new DependencyInfo (DependencyKind.ParameterType, method));
						markStep.MarkCustomAttributes (pd, new DependencyInfo (DependencyKind.ParameterAttribute, method));
						markStep.MarkMarshalSpec (pd, new DependencyInfo (DependencyKind.ParameterMarshalSpec, method));
					}
#pragma warning restore RS0030
				}

				if (method.HasOverrides) {
					var assembly = markStep.Context.Resolve (method.DeclaringType.Scope);
					// If this method is in a Copy, CopyUsed, or Save assembly, .overrides won't get swept and we need to keep all of them
					bool markAllOverrides = assembly != null && markStep.Annotations.GetAction (assembly) is AssemblyAction.Copy or AssemblyAction.CopyUsed or AssemblyAction.Save;
					foreach (MethodReference @base in method.Overrides) {
						// Method implementing a static interface method will have an override to it - note instance methods usually don't unless they're explicit.
						// Calling the implementation method directly has no impact on the interface, and as such it should not mark the interface or its method.
						// Only if the interface method is referenced, then all the methods which implemented must be kept, but not the other way round.
						if (!markAllOverrides &&
							markStep.Context.Resolve (@base) is MethodDefinition baseDefinition
							&& baseDefinition.DeclaringType.IsInterface && baseDefinition.IsStatic && method.IsStatic)
							continue;
						markStep.MarkMethod (@base, new DependencyInfo (DependencyKind.MethodImplOverride, method), ScopeStack.CurrentScope.Origin);
						markStep.MarkExplicitInterfaceImplementation (method, @base);
					}
				}

				markStep.MarkMethodSpecialCustomAttributes (method);

				if (method.IsVirtual)
					markStep.MarkMethodAsVirtual (method, ScopeStack.CurrentScope);

				markStep.MarkNewCodeDependencies (method);

				markStep.MarkBaseMethods (method);

				if (markStep.Annotations.GetOverrides (method) is IEnumerable<OverrideInformation> overrides) {
					foreach (var @override in overrides.Where (ov => markStep.Annotations.IsMarked (ov.Base) || markStep.IgnoreScope (ov.Base.DeclaringType.Scope))) {
						if (markStep.ShouldMarkOverrideForBase (@override))
							markStep.MarkOverrideForBaseMethod (@override);
					}
				}

				markStep.MarkType (method.ReturnType, new DependencyInfo (DependencyKind.ReturnType, method));
				markStep.MarkCustomAttributes (method.MethodReturnType, new DependencyInfo (DependencyKind.ReturnTypeAttribute, method));
				markStep.MarkMarshalSpec (method.MethodReturnType, new DependencyInfo (DependencyKind.ReturnTypeMarshalSpec, method));

				if (method.IsPInvokeImpl || method.IsInternalCall) {
					markStep.ProcessInteropMethod (method);
				}

				if (!method.HasBody || method.Body.CodeSize == 0) {
					markStep.ProcessUnsafeAccessorMethod (method);
				}

				if (markStep.ShouldParseMethodBody (method))
					markStep.MarkMethodBody (method.Body);

				if (method.DeclaringType.IsMulticastDelegate ()) {
					string? methodPair = null;
					if (method.Name == "BeginInvoke")
						methodPair = "EndInvoke";
					else if (method.Name == "EndInvoke")
						methodPair = "BeginInvoke";

					if (methodPair != null) {
						TypeDefinition declaringType = method.DeclaringType;
						markStep.MarkMethodIf (declaringType.Methods, m => m.Name == methodPair, new DependencyInfo (DependencyKind.MethodForSpecialType, declaringType), ScopeStack.CurrentScope.Origin);
					}
				}

				markStep.DoAdditionalMethodProcessing (method);

				markStep.ApplyPreserveMethods (method);
			}

			public override IEnumerable<CombinedDependencyListEntry>? GetConditionalStaticDependencies (NodeFactory context) => null;

			public override IEnumerable<CombinedDependencyListEntry>? SearchDynamicDependencies (List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory context) => null;

			protected override string GetName (NodeFactory context) => method.GetDisplayName ();

			object ILegacyTracingNode.DependencyObject => method;
		}
	}
}
