// Validates the ReadyToRun ExternalTypeMaps section produced for duplicate keys that differ only
// by trim target. PR #120519 changed compiler metadata collection from a 1:1 mapping to a 1:many
// mapping so these duplicate attributes compile and collapse to a single emitted key.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateAllTrimTargetsKept", typeof(DuplicateTarget), typeof(DuplicateTrimTargetA))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateAllTrimTargetsKept", typeof(DuplicateTarget), typeof(DuplicateTrimTargetB))]

// This pair exercises the "one trim target would be enough to keep the entry" branch in NativeAOT.
// crossgen2 still emits the collapsed key unconditionally, which this ReadyToRun test locks in.
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateSingleReferencedTrimTarget", typeof(DuplicateSingleTarget), typeof(DuplicateReferencedTrimTarget))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateSingleReferencedTrimTarget", typeof(DuplicateSingleTarget), typeof(DuplicateUnreferencedTrimTarget))]

// crossgen2 does not trim this entry away, but PR #120519 is still required so the duplicate key
// with two different trim targets collapses instead of being rejected as a duplicate mapping.
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateAllTrimTargetsUnreferenced", typeof(DuplicateUnreferencedTarget), typeof(DuplicateNeverReferencedTrimTargetA))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateAllTrimTargetsUnreferenced", typeof(DuplicateUnreferencedTarget), typeof(DuplicateNeverReferencedTrimTargetB))]

// These two keys cover both attribute orderings for the unconditional-overrides-conditional case.
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateUnconditionalFirst", typeof(DuplicateUnconditionalFirstTarget))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateUnconditionalFirst", typeof(DuplicateUnconditionalFirstTarget), typeof(DuplicateConditionalTrimTargetA))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateUnconditionalSecond", typeof(DuplicateUnconditionalSecondTarget), typeof(DuplicateConditionalTrimTargetB))]
[assembly: TypeMap<DuplicateTrimTargetsGroup>("DuplicateUnconditionalSecond", typeof(DuplicateUnconditionalSecondTarget))]

public static class DuplicateTrimTargets
{
    public static Type? GetMappedType(string key)
    {
        RootTrimTargets();

        IReadOnlyDictionary<string, Type> map = TypeMapping.GetOrCreateExternalTypeMapping<DuplicateTrimTargetsGroup>();
        return map.TryGetValue(key, out Type? targetType) ? targetType : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? GetUnknown() => null;

    private static void RootTrimTargets()
    {
        if (GetUnknown() is DuplicateTrimTargetA)
            Console.WriteLine(nameof(DuplicateTrimTargetA));
        if (GetUnknown() is DuplicateTrimTargetB)
            Console.WriteLine(nameof(DuplicateTrimTargetB));
        if (GetUnknown() is DuplicateReferencedTrimTarget)
            Console.WriteLine(nameof(DuplicateReferencedTrimTarget));
    }
}

public class DuplicateTrimTargetsGroup { }

public class DuplicateTarget { }
public class DuplicateSingleTarget { }
public class DuplicateUnreferencedTarget { }
public class DuplicateUnconditionalFirstTarget { }
public class DuplicateUnconditionalSecondTarget { }

public class DuplicateTrimTargetA { }
public class DuplicateTrimTargetB { }
public class DuplicateReferencedTrimTarget { }
public class DuplicateUnreferencedTrimTarget { }
public class DuplicateNeverReferencedTrimTargetA { }
public class DuplicateNeverReferencedTrimTargetB { }
public class DuplicateConditionalTrimTargetA { }
public class DuplicateConditionalTrimTargetB { }
