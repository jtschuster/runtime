// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

[assembly: TypeMap<MultipleTypeMapAssemblies>("1", typeof(object))]
[assembly: TypeMap<MultipleTypeMapAssemblies>("2", typeof(string))]

[assembly: TypeMapAssociation<MultipleTypeMapAssemblies>(typeof(object), typeof(string))]
[assembly: TypeMapAssociation<MultipleTypeMapAssemblies>(typeof(string), typeof(object))]

[assembly: TypeMap<DuplicateTypeMapEntriesAcrossAssemblies>("1", typeof(string))]

// This assembly contributes duplicate entries for the same key with different trim targets so the
// application assembly exercises TypeMapMetadata.MergePendingMap's per-trim-target merge loop.
[assembly: TypeMap<ValidDuplicateTypeMapEntriesAcrossAssemblies>("1", typeof(object), typeof(string))]
[assembly: TypeMap<ValidDuplicateTypeMapEntriesAcrossAssemblies>("1", typeof(object), typeof(Guid))]

[assembly: TypeMapAssemblyTarget<MultipleTypeMapAssemblies>("TypeMapLib3")] // Recursive check
[assembly: TypeMapAssemblyTarget<MultipleTypeMapAssemblies>("TypeMapLib4")]
[assembly: TypeMapAssemblyTarget<MultipleTypeMapAssemblies>("TypeMapApp")] // Circular check
