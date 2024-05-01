// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ILCompiler.DependencyAnalysisFramework;

namespace Mono.Linker.Steps
{
	public partial class MarkStep
	{
		internal sealed class EdgeVisitor : IDependencyAnalyzerLogEdgeVisitor<NodeFactory>
		{
			MarkStep _markStep;
			public EdgeVisitor (MarkStep markStep) => _markStep = markStep;

			private static bool ShouldBeLogged (DependencyNodeCore<NodeFactory> node, out object? dependencyObject)
			{
				switch(node) {
				case ILegacyTracingNode ltn:
					dependencyObject = ltn.DependencyObject;
					return true;
				default:
					dependencyObject = null;
					return false;
				};
			}

			public void VisitEdge (DependencyNodeCore<NodeFactory> nodeDepender, DependencyNodeCore<NodeFactory> nodeDependedOn, string reason)
			{
				if (!(ShouldBeLogged (nodeDependedOn, out var dependee) && ShouldBeLogged (nodeDepender, out var depender)))
					return;
				Debug.Assert (nodeDependedOn is not RootNode);
				DependencyInfo depInfo = new (NodeFactory.DependencyKinds[reason], depender);
				_markStep.Context.Tracer.AddDirectDependency (dependee!, depInfo, true);
			}

			public void VisitEdge (string root, DependencyNodeCore<NodeFactory> dependedOn)
			{
				Debug.Assert (dependedOn is RootNode || dependedOn is not ILegacyTracingNode);
				//if (!ShouldBeLogged (dependedOn))
				//return;
				//DependencyInfo depInfo = new (NodeFactory.DependencyKinds[root], null);
				//_markStep.Context.Tracer.AddDirectDependency (dependedOn, depInfo, true);
			}

			public void VisitEdge (DependencyNodeCore<NodeFactory> nodeDepender, DependencyNodeCore<NodeFactory> nodeDependerOther, DependencyNodeCore<NodeFactory> nodeDependedOn, string reason)
			{
				if (!(ShouldBeLogged (nodeDependedOn, out var dependee) && ShouldBeLogged(nodeDepender, out var depender) ))
					return;
				DependencyInfo depInfo = new (NodeFactory.DependencyKinds[reason], depender);
				_markStep.Context.Tracer.AddDirectDependency (dependee!, depInfo, true);
			}
		}
	}
}
