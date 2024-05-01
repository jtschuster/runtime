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
			readonly MethodDefinition _method;
			readonly DependencyInfo _reason;

			public MethodDefinitionNode (MethodDefinition method, DependencyInfo reason)
			{
				this._method = method;
				this._reason = reason;
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
				using var methodScope = ScopeStack.PushLocalScope (new MessageOrigin (_method));

				bool markedForCall =
					_reason.Kind == DependencyKind.DirectCall ||
					_reason.Kind == DependencyKind.VirtualCall ||
					_reason.Kind == DependencyKind.Newobj;

				foreach (Action<MethodDefinition> handleMarkMethod in MarkContext.MarkMethodActions)
					handleMarkMethod (_method);

				if (!markedForCall) {

					markStep.PreprocessMarkedType (_method.DeclaringType, new DependencyInfo (DependencyKind.DeclaringType, _method), ScopeStack.CurrentScope.Origin);
					yield return new (context.GetTypeNode (_method.DeclaringType), nameof(DependencyKind.DeclaringType));
					//markStep.MarkType (method.DeclaringType, new DependencyInfo (DependencyKind.DeclaringType, method));
				}

				markStep.MarkCustomAttributes (_method, new DependencyInfo (DependencyKind.CustomAttribute, _method));
				markStep.MarkSecurityDeclarations (_method, new DependencyInfo (DependencyKind.CustomAttribute, _method));

				markStep.MarkGenericParameterProvider (_method);

				if (_method.IsInstanceConstructor ()) {
					markStep.MarkRequirementsForInstantiatedTypes (_method.DeclaringType);
					markStep.Tracer.AddDirectDependency (_method.DeclaringType, new DependencyInfo (DependencyKind.InstantiatedByCtor, _method), marked: false);
				} else if (_method.IsStaticConstructor () && markStep.Annotations.HasLinkerAttribute<RequiresUnreferencedCodeAttribute> (_method))
					markStep.Context.LogWarning (ScopeStack.CurrentScope.Origin, DiagnosticId.RequiresUnreferencedCodeOnStaticConstructor, _method.GetDisplayName ());

				if (_method.IsConstructor) {
					if (!markStep.Annotations.ProcessSatelliteAssemblies && KnownMembers.IsSatelliteAssemblyMarker (_method))
						markStep.Annotations.ProcessSatelliteAssemblies = true;
				} else if (_method.TryGetProperty (out PropertyDefinition? property))
					markStep.MarkProperty (property, new DependencyInfo (PropagateDependencyKindToAccessors (_reason.Kind, DependencyKind.PropertyOfPropertyMethod), _method));
				else if (_method.TryGetEvent (out EventDefinition? @event)) {
					markStep.MarkEvent (@event, new DependencyInfo (PropagateDependencyKindToAccessors (_reason.Kind, DependencyKind.EventOfEventMethod), _method));
				}

				if (_method.HasMetadataParameters ()) {
#pragma warning disable RS0030 // MethodReference.Parameters is banned. It's easiest to leave the code as is for now
					foreach (ParameterDefinition pd in _method.Parameters) {
						markStep.MarkType (pd.ParameterType, new DependencyInfo (DependencyKind.ParameterType, _method));
						markStep.MarkCustomAttributes (pd, new DependencyInfo (DependencyKind.ParameterAttribute, _method));
						markStep.MarkMarshalSpec (pd, new DependencyInfo (DependencyKind.ParameterMarshalSpec, _method));
					}
#pragma warning restore RS0030
				}

				if (_method.HasOverrides) {
					var assembly = markStep.Context.Resolve (_method.DeclaringType.Scope);
					// If this method is in a Copy, CopyUsed, or Save assembly, .overrides won't get swept and we need to keep all of them
					bool markAllOverrides = assembly != null && markStep.Annotations.GetAction (assembly) is AssemblyAction.Copy or AssemblyAction.CopyUsed or AssemblyAction.Save;
					foreach (MethodReference @base in _method.Overrides) {
						// Method implementing a static interface method will have an override to it - note instance methods usually don't unless they're explicit.
						// Calling the implementation method directly has no impact on the interface, and as such it should not mark the interface or its method.
						// Only if the interface method is referenced, then all the methods which implemented must be kept, but not the other way round.
						if (!markAllOverrides &&
							markStep.Context.Resolve (@base) is MethodDefinition baseDefinition
							&& baseDefinition.DeclaringType.IsInterface && baseDefinition.IsStatic && _method.IsStatic)
							continue;
						markStep.MarkMethod (@base, new DependencyInfo (DependencyKind.MethodImplOverride, _method), ScopeStack.CurrentScope.Origin);
						markStep.MarkExplicitInterfaceImplementation (_method, @base);
					}
				}

				markStep.MarkMethodSpecialCustomAttributes (_method);

				if (_method.IsVirtual)
					markStep.MarkMethodAsVirtual (_method, ScopeStack.CurrentScope);

				markStep.MarkNewCodeDependencies (_method);

				markStep.MarkBaseMethods (_method);

				if (markStep.Annotations.GetOverrides (_method) is IEnumerable<OverrideInformation> overrides) {
					foreach (var @override in overrides.Where (ov => markStep.Annotations.IsMarked (ov.Base) || markStep.IgnoreScope (ov.Base.DeclaringType.Scope))) {
						if (markStep.ShouldMarkOverrideForBase (@override))
							markStep.MarkOverrideForBaseMethod (@override);
					}
				}

				markStep.MarkType (_method.ReturnType, new DependencyInfo (DependencyKind.ReturnType, _method));
				markStep.MarkCustomAttributes (_method.MethodReturnType, new DependencyInfo (DependencyKind.ReturnTypeAttribute, _method));
				markStep.MarkMarshalSpec (_method.MethodReturnType, new DependencyInfo (DependencyKind.ReturnTypeMarshalSpec, _method));

				if (_method.IsPInvokeImpl || _method.IsInternalCall) {
					markStep.ProcessInteropMethod (_method);
				}

				if (!_method.HasBody || _method.Body.CodeSize == 0) {
					markStep.ProcessUnsafeAccessorMethod (_method);
				}

				if (markStep.ShouldParseMethodBody (_method))
					markStep.MarkMethodBody (_method.Body);

				if (_method.DeclaringType.IsMulticastDelegate ()) {
					string? methodPair = null;
					if (_method.Name == "BeginInvoke")
						methodPair = "EndInvoke";
					else if (_method.Name == "EndInvoke")
						methodPair = "BeginInvoke";

					if (methodPair != null) {
						TypeDefinition declaringType = _method.DeclaringType;
						markStep.MarkMethodIf (declaringType.Methods, m => m.Name == methodPair, new DependencyInfo (DependencyKind.MethodForSpecialType, declaringType), ScopeStack.CurrentScope.Origin);
					}
				}

				markStep.DoAdditionalMethodProcessing (_method);

				markStep.ApplyPreserveMethods (_method);
			}

			public override IEnumerable<CombinedDependencyListEntry>? GetConditionalStaticDependencies (NodeFactory context) => null;

			public override IEnumerable<CombinedDependencyListEntry>? SearchDynamicDependencies (List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory context) => null;

			protected override string GetName (NodeFactory context) => _method.GetDisplayName ();

			object ILegacyTracingNode.DependencyObject => _method;
		}
	}
}
