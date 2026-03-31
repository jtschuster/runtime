// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace ILCompiler.DependencyAnalysisFramework
{
    public abstract class DependencyNode : IDependencyNode
    {
        private uint _markIndex;

        // Only DependencyNodeCore<T> is allowed to derive from this
        internal DependencyNode()
        { }

        internal void SetMark(uint markIndex)
        {
            Debug.Assert(markIndex != 0);
            Debug.Assert(_markIndex == 0);
            _markIndex = markIndex;
        }

        internal uint GetMarkIndex()
        {
            return _markIndex;
        }

        public bool Marked
        {
            get
            {
                return _markIndex != 0;
            }
        }

        public sealed override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj);
        }

        public sealed override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}
