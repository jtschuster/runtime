// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using ILCompiler.DependencyAnalysisFramework;
using Mono.Cecil;

namespace Mono.Linker.Steps
{
	public partial class MarkStep
	{
		internal class EventDefinitionNode : DependencyNodeCore<NodeFactory>
		{
			EventDefinition _event;
			public EventDefinitionNode(EventDefinition @event) => _event = @event;

			public override bool InterestingForDynamicDependencyAnalysis => false;

			public override bool HasDynamicDependencies => false;

			public override bool HasConditionalStaticDependencies => false;

			public override bool StaticDependenciesAreComputed => true;

			public override IEnumerable<CombinedDependencyListEntry>? GetConditionalStaticDependencies (NodeFactory context) => null;

			public override IEnumerable<DependencyListEntry> GetStaticDependencies (NodeFactory context)
			{
				context.MarkStep.ProcessEvent (_event);
				yield break;
			}

			public override IEnumerable<CombinedDependencyListEntry>? SearchDynamicDependencies (List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory context) => null;

			protected override string GetName (NodeFactory context) => _event.GetDisplayName();
		}
	}
}
