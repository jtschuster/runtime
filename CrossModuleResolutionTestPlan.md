# Cross-Module Reference Resolution Tests for R2R

## Problem Statement

The MutableModule's cross-module reference resolution has limited test coverage, especially for:
- Different assembly categories (version bubble, cross-module-inlineable-only, external/transitive)
- Composite mode (where `#:N` ModuleRef resolution is blocked by `m_pILModule == NULL`)
- ALC interactions (custom ALCs, composite single-ALC enforcement, JIT fallback)
- Edge cases (nested types, type forwarders, mixed-origin generics, field/method refs)

We need tests that cover every path through `GetNonNestedResolutionScope` and the corresponding
runtime resolution in `NativeManifestModule`, for both single-assembly and composite R2R modes.

## Background: Five Assembly Categories

| Category | In Compilation Set | In Version Bubble | CrossModuleInlineable |
|----------|:--:|:--:|:--:|
| A. Compilation Module | ✅ | ✅ | ✅ |
| B. Version Bubble (`--inputbubbleref`) | ❌ | ✅ | ✅ |
| C. CrossModule-Only (`--opt-cross-module`) | ❌ | ❌ | ✅ |
| D. External (transitive dep) | ❌ | ❌ | ❌ |
| E. CoreLib | Special | Special | Special |

### Critical Decision Tree for Type Tokenization

- `VersionsWithType(type) == true` → original TypeDef from type's module → `MODULE_ZAPSIG` in fixup → **works everywhere**
- `VersionsWithType(type) == false` → MutableModule creates TypeRef → resolution scope is ModuleRef:
  - CoreLib → `"System.Private.CoreLib"` → **works everywhere**
  - CrossModuleInlineable/VersionsWithModule → `#:N` → **works non-composite, fails composite**
  - External → `#AssemblyName:N` → **works non-composite, fails composite**

### Why `#:N` ModuleRef Instead of AssemblyRef

`NativeManifestModule` is `ModuleBase` (not `Module`) — it has no `PEAssembly`, no `Assembly`,
no ALC binder. It cannot independently resolve AssemblyRefs. `LoadAssemblyImpl` throws
`COR_E_BADIMAGEFORMAT` unconditionally. The `#:N` format routes loading through `m_pILModule`
(a real Module with an ALC binder). The `#AssemblyName:N` format preserves ALC-chaining through
intermediate modules.

---

## Existing Infrastructure

### 1. `src/tests/readytorun/tests/mainv1.csproj` — Cross-module inlining pattern
- Multiple assemblies compiled with explicit crossgen2 precommands
- Uses `--opt-cross-module:test`, `--map`, custom ALC loading
- `CLRTestBatchPreCommands`/`CLRTestBashPreCommands` pattern

### 2. `src/tests/readytorun/crossboundarylayout/` — Composite mode matrix
- Shell driver with composite/inputbubble/single mode permutations
- Focused on field layout, not cross-module references

### 3. `ILCompiler.Reflection.ReadyToRun` — Programmatic R2R reader
- `ReadyToRunReader`: `Methods`, `ImportSections`, `ManifestReferenceAssemblies`
- `ReadyToRunMethod`: `Fixups` → `FixupCell` → `ReadyToRunSignature`
- `InliningInfoSection2`: Cross-module inlining records with module indices
- `ReadyToRunSignature.ToString()`: Renders MODULE_ZAPSIG, ModuleOverride

### 4. R2RDump CLI
- `--header --sc` dumps section contents including InliningInfo
- `--in <assembly>` with `-r <refs>` for resolving references
- Text output only (no JSON), but parseable

### 5. `CLRTest.CrossGen.targets` auto-R2RDump
- Infrastructure runs `R2RDump --header --sc --val` after crossgen2 automatically

---

## R2R Compilation Validation Infrastructure

### Goal
Validate that cross-module inlining **actually occurred** and that the expected fixup
signatures reference the expected external modules.

### What the `--map` flag does NOT do
The crossgen2 `--map` flag produces a symbol/section layout map (RVA, length, node type) — a
linker-style map. It does **not** contain fixup signature details, MODULE_OVERRIDE references,
or inlining information. Existing tests (mainv1) use it only to confirm crossgen2 ran successfully.

### Approach: R2RDump for Compile-Time Validation

R2RDump (located at `src/coreclr/tools/r2rdump/`) is a CLI tool that reads R2R images and dumps
their contents, including import sections, fixup signatures, and inlining info. It uses the
`ILCompiler.Reflection.ReadyToRun` library internally.

#### Strategy 1: R2RDump Section Contents (compile-time, in precommands)
Run R2RDump with `--sc` (section contents) after crossgen2 to dump the `InliningInfo2` section.
Parse the text output to verify cross-module inliner/inlinee relationships with module indices.

```bash
# Run R2RDump after crossgen2 in the precommands
"$CORE_ROOT"/crossgen2/r2rdump --in main.dll --sc --rp "$CORE_ROOT" --rp . > main.r2rdump

# Verify the InliningInfo2 section contains cross-module entries referencing assemblyC
grep -q "module assemblyC" main.r2rdump || (echo "FAIL: no cross-module inlining from assemblyC" && exit 1)
```

The `InliningInfoSection2` decoder in R2RDump outputs lines like:
```
Inliners for inlinee 06000003 (module assemblyC):
  06000001
```
This shows that a method from `assemblyC` was inlined into `main`, confirming cross-module
inlining occurred at compile time.

#### Strategy 2: Runtime Correctness
Test methods call cross-module inlined code and verify return values. If fixups resolved
correctly, the methods return expected values. This proves end-to-end correctness.

#### Strategy 3: Programmatic Validation via ILCompiler.Reflection.ReadyToRun (future Phase 5)
For deeper validation, a managed test can reference `ILCompiler.Reflection.ReadyToRun` and use:
- `ReadyToRunReader.ManifestReferenceAssemblies` — verify expected assemblies in manifest
- `ReadyToRunMethod.Fixups` → `FixupCell.Signature` → `ReadyToRunSignature.ToString()` — verify MODULE_OVERRIDE references
- `InliningInfoSection2` — verify cross-module inlining records with module indices

This is more robust than text-parsing R2RDump output but requires building a managed validation
tool. Deferred to Phase 5.

### Validation Points Per Test Mode

| Validation | What It Proves |
|------------|---------------|
| R2RDump InliningInfo2 shows `module assemblyC` | Crossgen2 performed cross-module inlining |
| R2RDump ManifestMetadata lists assemblyC | Manifest AssemblyRef table includes the dependency |
| Test methods return correct values at runtime | Fixups resolved successfully end-to-end |
| Crossgen2 `--map` file exists | Crossgen2 ran successfully (basic sanity) |

---

## Test Design

### Assembly Graph

```
Assembly A (main, compilation target)
├── References B (version bubble, --inputbubbleref)
├── References C (cross-module-inlineable only, --opt-cross-module:assemblyC)
│   ├── C references D (external, transitive dependency)
│   └── C references CoreLib
└── References CoreLib

Assembly B (version bubble)
├── Defines types for version-bubble testing
└── References CoreLib

Assembly C (cross-module-inlineable, NOT in version bubble)
├── Defines inlineable methods that reference:
│   ├── C's own types
│   ├── D.DType (transitive dependency)
│   ├── D.Outer.Inner (nested type)
│   ├── CoreLib types (List<T>, etc.)
│   └── Type forwarded types
└── References D

Assembly D (external, NOT in any set)
├── Defines types referenced transitively through C
├── Defines nested types (D.Outer.Inner)
└── Defines types used as generic arguments

Assembly E (type forwarder source)
├── Has TypeForwarder for SomeForwardedType → D
└── C references SomeForwardedType via E's forwarder
```

### File Structure

```
src/tests/readytorun/crossmoduleresolution/
├── main/
│   ├── main.cs              — Test driver with all test methods
│   ├── main.csproj          — Main project (basic build, no crossgen2)
│   ├── main_crossmodule.csproj — Single R2R with --opt-cross-module:assemblyC
│   └── main_bubble.csproj   — Single R2R with --inputbubbleref assemblyB
├── assemblyB/
│   ├── B.cs                 — Version bubble types
│   └── assemblyB.csproj
├── assemblyC/
│   ├── C.cs                 — Cross-module-inlineable methods + types
│   └── assemblyC.csproj
├── assemblyD/
│   ├── D.cs                 — External/transitive types, nested types
│   └── assemblyD.csproj
├── assemblyE/
│   ├── E.cs                 — TypeForwarder assembly
│   └── assemblyE.csproj
└── README.md                — Test documentation
```

### Assembly Source Code

#### Assembly D (`assemblyD/D.cs`)
```csharp
namespace AssemblyD
{
    public class DType { public int Value => 42; }

    public class DClass
    {
        public static int StaticField = 100;
        public static int StaticMethod() => StaticField + 1;
    }

    public class Outer
    {
        public class Inner
        {
            public int GetValue() => 99;
        }
    }

    public class SomeForwardedType
    {
        public static string Name => "forwarded";
    }
}
```

#### Assembly E (`assemblyE/E.cs`)
```csharp
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(AssemblyD.SomeForwardedType))]
```

#### Assembly B (`assemblyB/B.cs`)
```csharp
namespace AssemblyB
{
    public class BType { public int Value => 7; }

    public class BClass
    {
        public static int StaticMethod() => 77;
        public static int StaticField = 777;
    }
}
```

#### Assembly C (`assemblyC/C.cs`)
```csharp
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AssemblyC
{
    public class CType { public int Value => 3; }

    public class CClass
    {
        public static int StaticField = 50;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseOwnType() => new CType().Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseDType() => new AssemblyD.DType().Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CallDMethod() => AssemblyD.DClass.StaticMethod();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadDField() => AssemblyD.DClass.StaticField;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseNestedType() => new AssemblyD.Outer.Inner().GetValue();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string UseForwardedType() => AssemblyD.SomeForwardedType.Name;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseGenericWithDType()
        {
            var list = new List<AssemblyD.DType>();
            list.Add(new AssemblyD.DType());
            return list[0].Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseCoreLibGeneric()
        {
            var list = new List<int> { 1, 2, 3 };
            return list.Count;
        }
    }

    public class CGeneric<T>
    {
        public T Value { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetCount() => 1;
    }

    public interface ICrossModule
    {
        int DoWork();
    }
}
```

#### Main Test Driver (`main/main.cs`)
```csharp
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

// Interface implementation for cross-module dispatch test
class CrossModuleImpl : AssemblyC.ICrossModule
{
    public int DoWork() => 42;
}

public static class CrossModuleResolutionTests
{
    // --- Version Bubble Tests (B) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_VersionBubble() => AssemblyB.BClass.StaticMethod();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_VersionBubble() => AssemblyB.BClass.StaticField;

    // --- Cross-Module-Only Tests (C) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_CrossModuleOnly() => AssemblyC.CClass.UseOwnType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTypeRef_Transitive() => AssemblyC.CClass.UseDType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestMethodCall_Transitive() => AssemblyC.CClass.CallDMethod();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_Transitive() => AssemblyC.CClass.ReadDField();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestNestedType_External() => AssemblyC.CClass.UseNestedType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string TestTypeForwarder() => AssemblyC.CClass.UseForwardedType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_MixedOrigin() => AssemblyC.CClass.UseGenericWithDType();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_CoreLib() => AssemblyC.CClass.UseCoreLibGeneric();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestGeneric_CrossModuleDefinition() => AssemblyC.CGeneric<int>.GetCount();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestFieldAccess_CrossModule() => AssemblyC.CClass.StaticField;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestInterfaceDispatch_CrossModule()
    {
        AssemblyC.ICrossModule impl = new CrossModuleImpl();
        return impl.DoWork();
    }

    // --- ALC Tests ---

    class TestLoadContext : AssemblyLoadContext
    {
        public TestLoadContext() : base(
            AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()).IsCollectible)
        { }

        public void TestLoadInSeparateALC()
        {
            // Load main assembly in a different ALC — R2R should still work (or JIT fallback)
            Assembly a = LoadFromAssemblyPath(
                Path.Combine(Directory.GetCurrentDirectory(), "main.dll"));
            Assert.AreEqual(GetLoadContext(a), this);
        }

        protected override Assembly Load(AssemblyName an) => null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TestALC_CustomLoad() => new TestLoadContext().TestLoadInSeparateALC();

    // --- Entry Point ---

    [Fact]
    public static int TestEntryPoint()
    {
        // Version bubble
        Assert.AreEqual(77, TestTypeRef_VersionBubble());
        Assert.AreEqual(777, TestFieldAccess_VersionBubble());

        // Cross-module-only (C's inlined methods)
        Assert.AreEqual(3, TestTypeRef_CrossModuleOnly());
        Assert.AreEqual(42, TestTypeRef_Transitive());
        Assert.AreEqual(101, TestMethodCall_Transitive());
        Assert.AreEqual(100, TestFieldAccess_Transitive());
        Assert.AreEqual(99, TestNestedType_External());
        Assert.AreEqual("forwarded", TestTypeForwarder());
        Assert.AreEqual(42, TestGeneric_MixedOrigin());
        Assert.AreEqual(3, TestGeneric_CoreLib());
        Assert.AreEqual(1, TestGeneric_CrossModuleDefinition());
        Assert.AreEqual(50, TestFieldAccess_CrossModule());
        Assert.AreEqual(42, TestInterfaceDispatch_CrossModule());

        // ALC
        TestALC_CustomLoad();

        return 100; // success
    }
}
```

### Test Modes (Separate .csproj Files)

#### Mode 1: `main_crossmodule.csproj` — Single R2R + `--opt-cross-module`

Crossgen2 precommands:
1. Copy IL DLLs to `IL_DLLS/`
2. Crossgen2 assemblyD (no special flags)
3. Crossgen2 assemblyB (no special flags)
4. Crossgen2 assemblyC with `-r assemblyD.dll`
5. Crossgen2 main with `--opt-cross-module:assemblyC --map -r assemblyB.dll -r assemblyC.dll -r assemblyD.dll`
6. **Validate**: check map file for MODULE_OVERRIDE references to assemblyC

#### Mode 2: `main_bubble.csproj` — Single R2R + `--inputbubbleref`

Crossgen2 precommands:
1. Copy IL DLLs to `IL_DLLS/`
2. Crossgen2 assemblyB (no special flags)
3. Crossgen2 main with `--inputbubbleref assemblyB --map -r assemblyB.dll -r assemblyC.dll -r assemblyD.dll`
4. **Validate**: check map file for MODULE_ZAPSIG references to assemblyB

---

## Phases

### Phase 1: Scaffolding (Current Scope)
1. Create Assembly D — external types, nested types, forwarded type definition
2. Create Assembly E — TypeForwarder to D
3. Create Assembly B — version bubble types
4. Create Assembly C — cross-module-inlineable methods with `[AggressiveInlining]`
5. Create main test driver with all test methods

### Phase 2: Single-Assembly R2R Tests (Current Scope)
6. Create `main_crossmodule.csproj` with crossgen2 precommands + map file validation
7. Create `main_bubble.csproj` with crossgen2 precommands + map file validation
8. Build and run — verify all tests pass in single R2R mode

### Phase 3: Composite Mode Tests (Deferred)
9. Create `main_composite.csproj` — `--composite` with A+B
10. Create `main_composite_crossmodule.csproj` — `--composite` A+B + `--opt-cross-module:assemblyC`
11. Determine expected behavior — should composite+crossmodule fail at crossgen2 time, at runtime, or JIT fallback?
12. Build and run — verify composite tests

### Phase 4: ALC Tests (Deferred)
13. Add ALC test cases — custom ALC loading, composite ALC mismatch fallback to JIT
14. Build and run — verify ALC scenarios

### Phase 5: Programmatic R2R Validation (Deferred)
15. Create managed validation tool using `ILCompiler.Reflection.ReadyToRun`
16. Validate `ManifestReferenceAssemblies` contains expected assemblies
17. Validate `ReadyToRunMethod.Fixups` contain expected MODULE_OVERRIDE signatures
18. Validate `InliningInfoSection2` records cross-module inlining with correct module indices

---

## Design Decisions

- **AggressiveInlining**: Yes — `[MethodImpl(AggressiveInlining)]` on C's methods to force cross-module inlining
- **Composite + cross-module behavior**: Eventually should be fixed (probably JIT fallback). Deferred to Phase 3
- **Test organization**: Separate .csproj files per mode (mainv1/mainv2 pattern)
- **Pre-commands**: Both Windows batch AND bash (following existing convention)
- **Priority**: Pri1 — specialized cross-module tests
- **Validation**: Map file + R2RDump for Phase 1-2; programmatic ILCompiler.Reflection.ReadyToRun for Phase 5
- **Return code**: 100 = success (matching CoreCLR test convention)
