// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using Mono.Linker.Tests.Cases.Reflection.Dependencies;

[assembly: TypeMap<TransitiveTypeMapGroup>("TransitiveEntry", typeof(TypeMapTransitiveTarget), typeof(TransitiveTrimTarget))]

namespace Mono.Linker.Tests.Cases.Reflection.Dependencies
{
    public class TypeMapTransitiveTarget;
}
