# Composite Mode Async Thunks in R2R

We unconditionally wrap MethodIL in MutableModuleWrappedMethodIL.

At runtime, we disable any tokens pointing from MutableModule to any Module except for SPCL

We also can't avoid creating MutableModule tokens for any TypeSystemEntities in the composite image. We inject methods
like Task.FromResult<T>(T obj) where T could be from within the composite image. This would necessitate a new entry in
the MutableModule for the generic method with the definition pointing to SPCL, but the generic argument a token within
the composite image.

We should start by enabling the AsyncVariantMethod method bodies. The real issue is the thunks, not the Async methods.

After that I don't see a way forward to emit these thunks without enabling MutableModule references within the composite
image version bubble. This was explicitly forbidden in the original implementation for cross-module inlining, but I
don't exactly know why. If we can find exactly why (and hopefully it's not a hard restriction that can't be worked
around), we can build safeguards and tests to validate the safety of it.

One alternative could be a special fixup that has the info required to construct the type without requiring module
tokens. Though a natural next step would be to deduplicate this information, which starts to look a lot like the
MutableModule.

## Issue: How do we know we can resolve a type or method in different situations

We may have Assembly A loaded and are doing eager fixups as we load Assembly B, then have a reference to a type in
Assembly D which goes through Assembly C. Do we need to load both assembly C and D? Are we able to do that in an eager
fixup while we load Assembly B? These are the types of issues we find

