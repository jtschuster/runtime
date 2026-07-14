// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using Mono.Linker.Tests.Cases.Expectations.Assertions;
using Mono.Linker.Tests.Cases.Expectations.Metadata;
using Mono.Linker.Tests.Cases.Reflection.Dependencies;

[assembly: TypeMapAssemblyTarget<AllConditionalGroupType>("conditional")]

namespace Mono.Linker.Tests.Cases.Reflection
{
    [SetupCompileBefore("allconditionalgroup.dll", new[] { "Dependencies/TypeMapAllConditionalGroupDep.cs" })]
    [SetupCompileBefore("conditional.dll", new[] { "Dependencies/TypeMapAllConditionalEntriesDep.cs" },
        references: new[] { "allconditionalgroup.dll" }, addAsReference: true)]
    [SetupLinkerAction("link", "System.Private.CoreLib")]
    [SetupLinkerArgument("--ignore-link-attributes", "false")]
    [Kept]
    [KeptAssembly("conditional.dll")]
    [RemovedAttributeInAssembly("test.exe", typeof(TypeMapAssemblyTargetAttribute<AllConditionalGroupType>))]
    class TypeMapAssemblyTargetRemovedWhenAssemblyMarkedForUnrelatedReason
    {
        [Kept]
        static void Main()
        {
            _ = TypeMapping.GetOrCreateExternalTypeMapping<AllConditionalGroupType>();
            // Mark the assembly for an unrelated reason
            new UnrelatedType();
        }
    }
}
