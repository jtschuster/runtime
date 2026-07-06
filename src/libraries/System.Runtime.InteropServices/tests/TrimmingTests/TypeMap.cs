// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: TypeMap<UsedTypeMap>("TrimTargetIsTarget", typeof(TargetAndTrimTarget), typeof(TargetAndTrimTarget))]
[assembly: TypeMap<UsedTypeMap>("TrimTargetIsUnrelated", typeof(TargetType), typeof(TrimTarget))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithDifferentTrimTargets", typeof(TargetType2), typeof(TrimTarget2))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithDifferentTrimTargets", typeof(TargetType2), typeof(TrimTarget3))]
// PR #120519 allows these duplicate-key entries because both attributes resolve to the same target type.
// Only DuplicateReferencedTrimTarget is observed below, so the entry must still be preserved.
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithOneReferencedTrimTarget", typeof(DuplicateReferencedTargetType), typeof(DuplicateReferencedTrimTarget))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithOneReferencedTrimTarget", typeof(DuplicateReferencedTargetType), typeof(DuplicateUnreferencedTrimTarget))]
// Neither trim target is observed for this duplicate key, so the entry and target type must be removed.
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithNoReferencedTrimTargets", typeof(DuplicateUnreferencedTargetType), typeof(DuplicateNeverReferencedTrimTarget1))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithNoReferencedTrimTargets", typeof(DuplicateUnreferencedTargetType), typeof(DuplicateNeverReferencedTrimTarget2))]
// These two keys cover both overload orderings for the unconditional short-circuit path.
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithUnconditionalEntryFirst", typeof(DuplicateUnconditionalTargetType))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithUnconditionalEntryFirst", typeof(DuplicateUnconditionalTargetType), typeof(DuplicateConditionalTrimTarget1))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithUnconditionalEntrySecond", typeof(DuplicateUnconditionalTargetType2), typeof(DuplicateConditionalTrimTarget2))]
[assembly: TypeMap<UsedTypeMap>("DuplicateMappingWithUnconditionalEntrySecond", typeof(DuplicateUnconditionalTargetType2))]
[assembly: TypeMap<UsedTypeMap>("TrimTargetIsUnreferenced", typeof(UnreferencedTargetType), typeof(UnreferencedTrimTarget))]
[assembly: TypeMapAssociation<UsedTypeMap>(typeof(SourceClass), typeof(ProxyType))]

[assembly: TypeMap<UnusedTypeMap>("UnusedName", typeof(UnusedTargetType), typeof(TrimTarget))]
[assembly: TypeMapAssociation<UsedTypeMap>(typeof(UnusedSourceClass), typeof(UnusedProxyType))]

if (args.Length > 1 && args[0] == "instantiate")
{
    Console.WriteLine("This code path should never actually be called. It exists exclusively for the trimmer to see that types are used in a way that it can't fully analyze.");
    // Execute some code here to ensure that our "trim target" types are seen as "possibly used".
    object t = Activator.CreateInstance(Type.GetType(args[1]));
    if (t is TargetAndTrimTarget)
    {
        Console.WriteLine("Type deriving from TargetAndTrimTarget instantiated.");
    }
    else if (t is TrimTarget)
    {
        Console.WriteLine("Type deriving from TrimTarget instantiated.");
    }
    else if (t is TrimTarget2)
    {
        Console.WriteLine("Type deriving from TrimTarget2 instantiated.");
    }
    else if (t is TrimTarget3)
    {
        Console.WriteLine("Type deriving from TrimTarget3 instantiated.");
    }
    else if (t is DuplicateReferencedTrimTarget)
    {
        Console.WriteLine("Type deriving from DuplicateReferencedTrimTarget instantiated.");
    }

    Console.WriteLine("Hash code of SourceClass instance: " + new SourceClass().GetHashCode());
    return -1;
}

IReadOnlyDictionary<string, Type> usedTypeMap = TypeMapping.GetOrCreateExternalTypeMapping<UsedTypeMap>();

if (!usedTypeMap.TryGetValue("TrimTargetIsTarget", out Type targetAndTrimTargetType))
{
    Console.WriteLine("TrimTargetIsTarget not found in used type map.");
    return 1;
}

if (targetAndTrimTargetType != GetTypeWithoutTrimAnalysis(nameof(TargetAndTrimTarget)))
{
    Console.WriteLine("TrimTargetIsTarget type does not match expected type.");
    return 2;
}

if (!usedTypeMap.TryGetValue("TrimTargetIsUnrelated", out Type targetType))
{
    Console.WriteLine("TrimTargetIsUnrelated not found in used type map.");
    return 3;
}

if (targetType != GetTypeWithoutTrimAnalysis(nameof(TargetType)))
{
    Console.WriteLine("TrimTargetIsUnrelated type does not match expected type.");
    return 4;
}

if (GetTypeWithoutTrimAnalysis(nameof(TrimTarget)) is not null)
{
    Console.WriteLine("TrimTarget should not be preserved if the only place that would preserve it is a check that is optimized away.");
    return 5;
}

if (usedTypeMap.TryGetValue("TrimTargetIsUnreferenced", out _))
{
    Console.WriteLine("TrimTargetIsUnreferenced should not be found in used type map.");
    return 6;
}

IReadOnlyDictionary<Type, Type> usedProxyTypeMap = TypeMapping.GetOrCreateProxyTypeMapping<UsedTypeMap>();
if (!usedProxyTypeMap.TryGetValue(typeof(SourceClass), out Type proxyType))
{
    Console.WriteLine("SourceClass not found in used proxy type map.");
    return 7;
}

if (proxyType != GetTypeWithoutTrimAnalysis(nameof(ProxyType)))
{
    Console.WriteLine("SourceClass proxy type does not match expected type.");
    return 8;
}

if (GetTypeWithoutTrimAnalysis(nameof(UnusedTargetType)) is not null)
{
    Console.WriteLine("UnusedTargetType should not be preserved if the external type map is not used and it is not referenced otherwise even if the entry's trim target is kept.");
    return 9;
}

if (GetTypeWithoutTrimAnalysis(nameof(UnusedProxyType)) is not null)
{
    Console.WriteLine("UnusedProxyType should not be preserved if the proxy type map is not used and it is not referenced otherwise even if the entry's source type is kept.");
    return 10;
}

if (!usedTypeMap.TryGetValue("DuplicateMappingWithDifferentTrimTargets", out Type duplicatedTarget))
{
    Console.WriteLine("Could not find duplicated target type");
    return 11;
}

if (duplicatedTarget != GetTypeWithoutTrimAnalysis(nameof(TargetType2)))
{
    Console.WriteLine("DuplicateMappingWithDifferentTrimTargets resolved to the wrong target type.");
    return 12;
}

// PR #120519 changed duplicate-key handling to keep the entry if any trim target is marked.
if (!usedTypeMap.TryGetValue("DuplicateMappingWithOneReferencedTrimTarget", out Type duplicateReferencedTarget))
{
    Console.WriteLine("DuplicateMappingWithOneReferencedTrimTarget should be preserved when one trim target is referenced.");
    return 13;
}

if (duplicateReferencedTarget != GetTypeWithoutTrimAnalysis(nameof(DuplicateReferencedTargetType)))
{
    Console.WriteLine("DuplicateMappingWithOneReferencedTrimTarget resolved to the wrong target type.");
    return 14;
}

if (GetTypeWithoutTrimAnalysis(nameof(DuplicateUnreferencedTrimTarget)) is not null)
{
    Console.WriteLine("DuplicateUnreferencedTrimTarget should not be preserved when a different duplicate trim target keeps the entry.");
    return 15;
}

// None of these trim targets are referenced, so PR #120519 should still let trimming remove the merged entry.
if (usedTypeMap.TryGetValue("DuplicateMappingWithNoReferencedTrimTargets", out _))
{
    Console.WriteLine("DuplicateMappingWithNoReferencedTrimTargets should not be found in used type map.");
    return 16;
}

if (GetTypeWithoutTrimAnalysis(nameof(DuplicateUnreferencedTargetType)) is not null)
{
    Console.WriteLine("DuplicateUnreferencedTargetType should not be preserved when all duplicate trim targets are trimmed.");
    return 17;
}

// A null trim target from the overload without a trim target should make the merged entry unconditional.
if (!usedTypeMap.TryGetValue("DuplicateMappingWithUnconditionalEntryFirst", out Type unconditionalFirstTarget))
{
    Console.WriteLine("DuplicateMappingWithUnconditionalEntryFirst should be present because one duplicate entry is unconditional.");
    return 18;
}

if (unconditionalFirstTarget != GetTypeWithoutTrimAnalysis(nameof(DuplicateUnconditionalTargetType)))
{
    Console.WriteLine("DuplicateMappingWithUnconditionalEntryFirst resolved to the wrong target type.");
    return 19;
}

if (!usedTypeMap.TryGetValue("DuplicateMappingWithUnconditionalEntrySecond", out Type unconditionalSecondTarget))
{
    Console.WriteLine("DuplicateMappingWithUnconditionalEntrySecond should be present even when the unconditional overload appears second.");
    return 20;
}

if (unconditionalSecondTarget != GetTypeWithoutTrimAnalysis(nameof(DuplicateUnconditionalTargetType2)))
{
    Console.WriteLine("DuplicateMappingWithUnconditionalEntrySecond resolved to the wrong target type.");
    return 21;
}

if (GetTypeWithoutTrimAnalysis(nameof(DuplicateConditionalTrimTarget1)) is not null)
{
    Console.WriteLine("DuplicateConditionalTrimTarget1 should not be preserved when the unconditional duplicate short-circuits the entry.");
    return 22;
}

if (GetTypeWithoutTrimAnalysis(nameof(DuplicateConditionalTrimTarget2)) is not null)
{
    Console.WriteLine("DuplicateConditionalTrimTarget2 should not be preserved when the unconditional duplicate appears second.");
    return 23;
}

return 100;

[MethodImpl(MethodImplOptions.NoInlining)]
static Type GetTypeWithoutTrimAnalysis(string typeName)
{
    return Type.GetType(typeName, throwOnError: false);
}

class UsedTypeMap;
class TargetAndTrimTarget;
class TargetType;
class TargetType2;
class TrimTarget;
class TrimTarget2;
class TrimTarget3;
class DuplicateReferencedTargetType;
class DuplicateReferencedTrimTarget;
class DuplicateUnreferencedTrimTarget;
class DuplicateUnreferencedTargetType;
class DuplicateNeverReferencedTrimTarget1;
class DuplicateNeverReferencedTrimTarget2;
class DuplicateUnconditionalTargetType;
class DuplicateUnconditionalTargetType2;
class DuplicateConditionalTrimTarget1;
class DuplicateConditionalTrimTarget2;
class UnreferencedTargetType;
class UnreferencedTrimTarget;
class SourceClass;
class ProxyType;

class UnusedTypeMap;
class UnusedTargetType;
class UnusedSourceClass;
class UnusedProxyType;
