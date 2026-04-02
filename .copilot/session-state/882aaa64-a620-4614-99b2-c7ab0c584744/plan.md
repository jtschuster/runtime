# R2R Cross-Module × Async × Composite Test Plan

## Problem

We need comprehensive test coverage for the intersection of three features in crossgen2 R2R output:
1. **Cross-module inlining** — methods from one assembly inlined into another
2. **Runtime-async** — async method compilation with [ASYNC] variants, continuation layouts, resumption stubs
3. **Composite mode** — multiple assemblies merged into one R2R image

Each combination exercises different code paths in crossgen2 (MutableModule token encoding, manifest metadata, inlining info sections, continuation layout fixups) and the runtime (LoadDynamicInfoEntry, assembly loading, fixup resolution).

## Existing Tests (9, all passing)

| # | Test | Composite | Cross-Module | Async | Key Validations |
|---|------|-----------|-------------|-------|-----------------|
| 1 | BasicCrossModuleInlining | No | Direct sync | No | ManifestRef, CrossModuleInlinedMethod, CrossModuleInliningInfo |
| 2 | TransitiveReferences | No | Transitive sync | No | ManifestRef×2, CrossModuleInlinedMethod |
| 3 | AsyncCrossModuleInlining | No | Direct (async inlinee) | No¹ | ManifestRef, CrossModuleInlinedMethod |
| 4 | CompositeBasic | Yes | Implicit | No | ManifestRef |
| 5 | RuntimeAsyncMethodEmission | No | None | Yes | AsyncVariant×3 |
| 6 | RuntimeAsyncContinuationLayout | No | None | Yes | AsyncVariant, ContinuationLayout, ResumptionStubFixup |
| 7 | RuntimeAsyncDevirtualize | No | None | Yes | AsyncVariant |
| 8 | RuntimeAsyncNoYield | No | None | Yes | AsyncVariant×2 |
| 9 | RuntimeAsyncCrossModule | No | Direct | Yes | ManifestRef, AsyncVariant |

¹ AsyncCrossModuleInlining inlines regular async methods (Task-returning) but does NOT use the runtime-async feature flag.

## Coverage Gaps

### Gap 1: Composite + detailed inlining validation
CompositeBasic only checks ManifestRef. No test validates that inlining actually occurs between assemblies in composite mode or checks InliningInfo2 / CrossModuleInlineInfo sections.

### Gap 2: Composite + runtime-async
No test combines `--composite` with the `runtime-async` feature flag.

### Gap 3: Composite + runtime-async + cross-module inlining
The full intersection — async methods from one assembly inlined into another within a composite image. Exercises MutableModule token encoding for cross-module async continuation layouts in composite metadata.

### Gap 4: Non-composite runtime-async + cross-module inlining with continuations
RuntimeAsyncCrossModule validates ManifestRef + AsyncVariant but doesn't test --opt-cross-module inlining of async methods with GC refs across await points.

### Gap 5: Transitive + async
No test combines transitive cross-module references with runtime-async.

### Gap 6: Multi-step compilation
No test uses the multi-compilation model (compile composite first, then non-composite referencing those assemblies).

### Gap 7: Composite + async devirtualization
RuntimeAsyncDevirtualize is single-assembly. Cross-module async devirtualization in composite is untested.

### Gap 8: Input bubble boundaries in composite
No test uses --input-bubble + --inputbubbleref to test version bubble boundaries.

## New Tests

### Tier 1: Critical (unique code paths)

**10. CompositeCrossModuleInlining** — Composite, sync, validates that Lib methods are inlined into Main
- Config: `--composite`
- Validates: ManifestRef, CrossModuleInlineInfo (CrossModuleInliningForCrossModuleDataOnly), InliningInfo2
- Reuse existing source: BasicInlining.cs + InlineableLib.cs

**11. CompositeAsync** — Composite + runtime-async baseline
- Config: `--composite`, `runtime-async=on`
- Validates: AsyncVariant for methods in both assemblies, ManifestRef
- New source: AsyncCompositeLib.cs + CompositeAsyncMain.cs

**12. CompositeAsyncCrossModuleInlining** — THE full intersection test
- Config: `--composite`, `runtime-async=on`
- Validates: AsyncVariant, ContinuationLayout, CrossModuleInlineInfo, ManifestRef
- New source: needs async methods with GC refs across await in cross-module context

**13. AsyncCrossModuleContinuation** — Non-composite, runtime-async + cross-module + continuations
- Config: `--opt-cross-module`, `runtime-async=on`
- Validates: AsyncVariant, ContinuationLayout, CrossModuleInlinedMethod, ManifestRef
- New source: AsyncDepLibContinuation.cs + AsyncCrossModuleContinuation.cs

**14. MultiStepCompositeAndNonComposite** — Two-step compilation
- Step 1: Compile A+B as composite → validate composite output
- Step 2: Compile C non-composite with --ref A B --opt-cross-module A → validate C's output
- New source: needs a consumer assembly referencing composite-compiled libs

### Tier 2: Important (depth coverage)

**15. CompositeAsyncDevirtualize** — Composite + runtime-async + devirtualization across modules
- Config: `--composite`, `runtime-async=on`
- Validates: AsyncVariant for devirtualized calls
- New source: AsyncInterfaceLib.cs + CompositeAsyncDevirtMain.cs

**16. CompositeTransitive** — Composite with 3 assemblies in A→B→C chain
- Config: `--composite`
- Validates: ManifestRef×3, CrossModuleInlineInfo for transitive inlining
- Reuse existing source: ExternalLib.cs + InlineableLibTransitive.cs + TransitiveReferences.cs

**17. AsyncCrossModuleTransitive** — Non-composite, runtime-async + transitive cross-module
- Config: `--opt-cross-module`, `runtime-async=on`
- Validates: ManifestRef×2, AsyncVariant, CrossModuleInlinedMethod
- New source: AsyncExternalLib.cs + AsyncTransitiveLib.cs + AsyncTransitiveMain.cs

### Tier 3: Extended coverage

**18. CompositeAsyncTransitive** — Composite + async + transitive (3 assemblies)
- Config: `--composite`, `runtime-async=on`
- Validates: ManifestRef×3, AsyncVariant, CrossModuleInlineInfo
- Can reuse Tier 2 async transitive source with composite config

**19. MultiStepCompositeAndNonCompositeAsync** — Multi-step with runtime-async
- Step 1: Compile AsyncLib+AsyncMain as composite
- Step 2: Compile AsyncConsumer non-composite with --opt-cross-module, runtime-async=on
- Validates: Both outputs have correct async variants and cross-module references

## Notes

- In composite mode, `--opt-cross-module` is NOT used — cross-module inlining is implicit
- Composite emits CrossModuleInlineInfo with `CrossModuleInliningForCrossModuleDataOnly` (cross-module entries only); non-composite emits `CrossModuleAllMethods` (all entries)
- InliningInfo2 is composite-only, per-module, same-module inlining
- For multi-step tests, crossgen2 reads IL metadata from --ref assemblies, not R2R output
- All tests validate R2R metadata only — they don't execute the compiled code
