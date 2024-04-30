// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ILCompiler.DependencyAnalysisFramework;

namespace Mono.Linker.Steps
{
	public partial class MarkStep
	{
		internal sealed class RootNode : DependencyNodeCore<NodeFactory>, ILegacyTracingNode
		{
			readonly DependencyNodeCore<NodeFactory> dependee;
			readonly string reason;
			readonly object? depender;

			public RootNode (DependencyNodeCore<NodeFactory> dep, string reason, object? depender)
			{
				this.dependee = dep;
				this.reason = reason;
				this.depender = depender;
			}

			public override bool InterestingForDynamicDependencyAnalysis => false;

			public override bool HasDynamicDependencies => false;

			public override bool HasConditionalStaticDependencies => false;

			public override bool StaticDependenciesAreComputed => true;

			public override IEnumerable<DependencyListEntry>? GetStaticDependencies (NodeFactory context)
			{
				return [new DependencyListEntry (dependee, reason)];
			}

			public override IEnumerable<CombinedDependencyListEntry>? GetConditionalStaticDependencies (NodeFactory context) => null;
			public override IEnumerable<CombinedDependencyListEntry>? SearchDynamicDependencies (List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory context) => null;
			protected override string GetName (NodeFactory context) => depender?.ToString() ?? "Unknown";
			object? ILegacyTracingNode.DependencyObject => depender;
		}
	}
}
