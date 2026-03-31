// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace ILCompiler.DependencyAnalysisFramework
{
    /// <summary>
    /// Reusable buffer for collecting dependency edges in Structure-of-Arrays layout.
    /// When reason capture is disabled, the reasons array is not allocated,
    /// reducing memory usage per edge from 16 bytes to 8 bytes.
    /// </summary>
    public sealed class DependencyCollector<DependencyContextType>
    {
        private DependencyNodeCore<DependencyContextType>[] _nodes;
        private string[] _reasons;
        private int _count;

        public DependencyCollector(bool captureReasons, int initialCapacity = 32)
        {
            _nodes = new DependencyNodeCore<DependencyContextType>[initialCapacity];
            _reasons = captureReasons ? new string[initialCapacity] : null;
        }

        public void Add(DependencyNodeCore<DependencyContextType> node, string reason)
        {
            if (_count == _nodes.Length)
                Grow();

            _nodes[_count] = node;
            if (_reasons is not null)
                _reasons[_count] = reason;
            _count++;
        }

        public void Clear()
        {
            Array.Clear(_nodes, 0, _count);
            if (_reasons is not null)
                Array.Clear(_reasons, 0, _count);
            _count = 0;
        }

        public int Count => _count;
        public DependencyNodeCore<DependencyContextType> GetNode(int index) => _nodes[index];
        public string GetReason(int index) => _reasons is not null ? _reasons[index] : null;

        private void Grow()
        {
            int newCapacity = _nodes.Length * 2;

            var newNodes = new DependencyNodeCore<DependencyContextType>[newCapacity];
            Array.Copy(_nodes, newNodes, _count);
            _nodes = newNodes;

            if (_reasons is not null)
            {
                var newReasons = new string[newCapacity];
                Array.Copy(_reasons, newReasons, _count);
                _reasons = newReasons;
            }
        }
    }

    /// <summary>
    /// Reusable buffer for collecting combined dependency edges (conditional/dynamic)
    /// in Structure-of-Arrays layout.
    /// </summary>
    public sealed class CombinedDependencyCollector<DependencyContextType>
    {
        private DependencyNodeCore<DependencyContextType>[] _nodes;
        private DependencyNodeCore<DependencyContextType>[] _otherReasonNodes;
        private string[] _reasons;
        private int _count;

        public CombinedDependencyCollector(bool captureReasons, int initialCapacity = 16)
        {
            _nodes = new DependencyNodeCore<DependencyContextType>[initialCapacity];
            _otherReasonNodes = new DependencyNodeCore<DependencyContextType>[initialCapacity];
            _reasons = captureReasons ? new string[initialCapacity] : null;
        }

        public void Add(DependencyNodeCore<DependencyContextType> node,
                        DependencyNodeCore<DependencyContextType> otherReasonNode,
                        string reason)
        {
            if (_count == _nodes.Length)
                Grow();

            _nodes[_count] = node;
            _otherReasonNodes[_count] = otherReasonNode;
            if (_reasons is not null)
                _reasons[_count] = reason;
            _count++;
        }

        public void Clear()
        {
            Array.Clear(_nodes, 0, _count);
            Array.Clear(_otherReasonNodes, 0, _count);
            if (_reasons is not null)
                Array.Clear(_reasons, 0, _count);
            _count = 0;
        }

        public int Count => _count;
        public DependencyNodeCore<DependencyContextType> GetNode(int index) => _nodes[index];
        public DependencyNodeCore<DependencyContextType> GetOtherReasonNode(int index) => _otherReasonNodes[index];
        public string GetReason(int index) => _reasons is not null ? _reasons[index] : null;

        private void Grow()
        {
            int newCapacity = _nodes.Length * 2;

            var newNodes = new DependencyNodeCore<DependencyContextType>[newCapacity];
            Array.Copy(_nodes, newNodes, _count);
            _nodes = newNodes;

            var newOtherNodes = new DependencyNodeCore<DependencyContextType>[newCapacity];
            Array.Copy(_otherReasonNodes, newOtherNodes, _count);
            _otherReasonNodes = newOtherNodes;

            if (_reasons is not null)
            {
                var newReasons = new string[newCapacity];
                Array.Copy(_reasons, newReasons, _count);
                _reasons = newReasons;
            }
        }
    }
}
