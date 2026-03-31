// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.DependencyAnalysisFramework
{
    public interface IDependencyAnalysisMarkStrategy<DependencyContextType>
    {
        /// <summary>
        /// Use the provided node, reasonNode, reasonNode2, and reason to mark a node
        /// </summary>
        /// <param name="node"></param>
        /// <param name="reasonNode"></param>
        /// <param name="reasonNode2"></param>
        /// <param name="reason"></param>
        /// <returns>true if the node is newly marked</returns>
        bool MarkNode(DependencyNodeCore<DependencyContextType> node, DependencyNodeCore<DependencyContextType> reasonNode, DependencyNodeCore<DependencyContextType> reasonNode2, string reason);

        void VisitLogNodes(IEnumerable<DependencyNodeCore<DependencyContextType>> nodeList, IDependencyAnalyzerLogNodeVisitor<DependencyContextType> logNodeVisitor);

        void VisitLogEdges(IEnumerable<DependencyNodeCore<DependencyContextType>> nodeList, IDependencyAnalyzerLogEdgeVisitor<DependencyContextType> logEdgeVisitor);

        void AttachContext(DependencyContextType context);

        /// <summary>
        /// Indicates whether this strategy needs reason strings for dependency edges.
        /// When false, the analyzer can skip capturing and passing reason strings,
        /// reducing memory usage and improving cache utilization.
        /// </summary>
        static virtual bool NeedsReasons => true;
    }
}
