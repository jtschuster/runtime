// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    public struct ReadyToRunSection
    {
        /// <summary>
        /// The ReadyToRun section type
        /// </summary>
        public ReadyToRunSectionType Type { get; set; }

        /// <summary>
        /// The RVA to the section
        /// </summary>
        public ImageRVA RelativeVirtualAddress { get; set; }

        /// <summary>
        /// The size of the section
        /// </summary>
        public int Size { get; set; }

        public ReadyToRunSection(ReadyToRunSectionType type, ImageRVA rva, int size)
        {
            Type = type;
            RelativeVirtualAddress = rva;
            Size = size;
        }

        public override string ToString()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Opaque handle representing an RVA pointing to the start of a ReadyToRun section.</summary>
    public enum ImageRVA {}
}
