# Cross-Module Resolution Tests

These tests verify R2R (ReadyToRun) cross-module reference resolution across different
assembly categories and crossgen2 compilation modes.

## Assembly Graph

- **Assembly A** (`main/`) — Main test driver, compilation target
- **Assembly B** (`assemblyB/`) — Version bubble (`--inputbubbleref`)
- **Assembly C** (`assemblyC/`) — Cross-module-inlineable only (`--opt-cross-module`)
- **Assembly D** (`assemblyD/`) — External/transitive dependency (not in any set)
- **Assembly E** (`assemblyE/`) — TypeForwarder assembly (forwards to D)

## Test Modes

| Variant | Crossgen2 Flags | What It Tests |
|---------|----------------|---------------|
| `main_crossmodule` | `--opt-cross-module:assemblyC` | MutableModule `#:N` and `#D:N` ModuleRef resolution |
| `main_bubble` | `--inputbubbleref:assemblyB.dll` | Version bubble MODULE_ZAPSIG encoding |

## Test Cases

Each test method exercises a different cross-module reference scenario:
- TypeRef from version bubble (B) and cross-module-only (C)
- Method calls and field accesses across module boundaries
- Transitive dependencies (C → D)
- Nested types, type forwarders, mixed-origin generics
- Interface dispatch with cross-module interfaces
- Custom ALC loading

## Building

These are pri1 tests. Build with `-priority1`:

```bash
src/tests/build.sh -Test src/tests/readytorun/crossmoduleresolution/main/main_crossmodule.csproj x64 Release -priority1
```
