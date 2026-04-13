# myR2RDump Implementation Notes

## Overview

A standalone R2R (Ready-to-Run) image dump tool that decodes and prints all sections,
methods, types, fixups, and GC info from composite and non-composite R2R images.
Uses the `ILCompiler.Reflection.ReadyToRun.Structural` library for raw section parsing
and a `DiskAssemblyResolver` for cross-module metadata resolution.

## Key Architecture Decisions

### Two MetadataReader Contexts in Signature Decoding

The most subtle part of the implementation is handling `MODULE_ZAPSIG` correctly.
R2R signature decoding requires **two separate MetadataReader contexts**:

1. **`mdReader`** — the "current" context for resolving type tokens (TypeDef, TypeRef)
   and method/field tokens (MemberRef, MethodDef).
2. **`outerMdReader`** — the "outer" context, preserved across MODULE_ZAPSIG boundaries,
   used specifically for GENERICINST type arguments.

This mirrors the reference `SignatureDecoder` which maintains `_metadataReader` and
`_outerReader` as separate fields.

### MODULE_ZAPSIG Handling Differs by Call Path

There are **two completely separate code paths** for decoding methods, and MODULE_ZAPSIG
must be handled differently in each:

#### 1. Import Fixups (fixup signatures)

- The fixup byte has a `ModuleOverride` bit that sets `mdReader` to the fixup-level
  module (e.g., `shared-generic` with 127 MemberRef rows).
- MODULE_ZAPSIG inside the owning type (e.g., `MODULE_ZAPSIG(12, GENERICINST(...))`)
  is handled **internally** by `DecodeType` — it only affects type token resolution
  within that type, NOT the method/field RID lookup.
- The MemberRef/MethodDef RID is looked up using the **fixup-level** module's reader.
- **Do NOT peek** for MODULE_ZAPSIG to update mdReader.

#### 2. InstanceMethodEntryPoints (hash table entries)

- There is no fixup byte. The initial `mdReader` is the manifest metadata (which has
  0 MethodDef rows in composite images).
- MODULE_ZAPSIG must be **peeked** from the owning type to update `mdReader` to the
  correct component module's reader. This matches the reference code's
  `GetMetadataReaderFromModuleOverride()` in `DecodeMethodSignature`.
- The MemberRef/MethodDef RID is looked up using the **peeked** module's reader.

This is controlled by the `peekModuleOverride` parameter on `DecodeMethod`.

### GENERICINST Type Arguments Use outerMdReader

When decoding `GENERICINST(baseType, typeArg1, typeArg2, ...)`:
- The base type uses `mdReader` (which may have been changed by MODULE_ZAPSIG)
- The type arguments use `outerMdReader` (the fixup-level or pre-MODULE_ZAPSIG reader)

This is because type arguments reference tokens from the outer module context, not
the MODULE_ZAPSIG target module. The reference code creates an `outerDecoder` with
`_metadataReader = _outerReader` for decoding type args.

### Module Index Mapping (Composite R2R)

For R2R version ≥ 6.3 (`ComponentAssemblyIndicesStartAtTwo`):
- Module 0 = self (composite image manifest)
- Module 1 = manifest metadata itself
- Module 2+ = manifest metadata's AssemblyRef table entries (1-based: AssemblyRef row 1 = module index 2)

### Compressed Integer Encodings

Two different compressed integer formats exist in the codebase:

- **ECMA-335 format** (used for signature decoding): bit 7/6 based.
  `(val & 0x80) == 0` → 1 byte, `(val & 0xC0) == 0x80` → 2 bytes, else 4 bytes.
- **NativeFormat** (used by `NativeReader.DecodeUnsigned` for hash tables): bit 0/1 based.
  `(val & 1) == 0` → 1 byte, `(val & 2) == 0` → 2 bytes, else 4 bytes.

Signature decoding (our `ReadCompressedUInt`) uses ECMA-335 format, matching the
reference `SignatureDecoder.ReadUInt`.

## ReadyToRunMethodSigFlags Reference

```
UnboxingStub      = 0x01
InstantiatingStub = 0x02
MethodInstantiation = 0x04
SlotInsteadOfToken = 0x08
MemberRefToken    = 0x10
Constrained       = 0x20
OwnerType         = 0x40
UpdateContext      = 0x80
```

## Build & Run

```bash
# Build the Structural library (dependency)
cd src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun.Structural
dotnet build -c Release

# Build myR2RDump
cd src/coreclr/tools/aot/myR2RDump
dotnet build -c Release

# Run on a composite R2R image (--rp not needed, assembly resolution is automatic)
dotnet run -c Release -- path/to/composite-r2r.dll
```

## Files

- **Program.cs** — Main dump logic: section iteration, signature decoding, type/method
  formatting, import fixup decoding, InstanceMethodEntryPoints decoding.
- **DiskAssemblyResolver.cs** — Resolves module indices to MetadataReaders by scanning
  the R2R manifest metadata's AssemblyRef table and finding assemblies on disk.
- **IAssemblyResolver.cs** — Interface for module index → MetadataReader resolution.

## Bugs Found & Fixed During Development

1. **MODULE_ZAPSIG peek corrupting mdReader for import fixups** — The peek was changing
   mdReader from the fixup-level module to the MODULE_ZAPSIG target, causing MemberRef
   OOB errors (e.g., MemberRef#95 looked up in a module with only 31 rows).
   Fix: Only peek for InstanceMethodEntryPoints, not import fixups.

2. **GENERICINST type args using wrong MetadataReader** — After MODULE_ZAPSIG changed
   mdReader, type arguments were decoded with the wrong module's reader, causing
   TypeDef/TypeRef OOB errors.
   Fix: Thread `outerMdReader` through DecodeType/DecodeGenericInst/DecodeModuleZapSigType.

3. **InstanceMethodEntryPoints using manifest reader** — For composite images, the
   initial mdReader was the manifest (0 MethodDef rows). Without the MODULE_ZAPSIG peek,
   all MethodDef lookups failed.
   Fix: Add `peekModuleOverride: true` for the InstanceMethodEntryPoints path.

4. **DecodeModuleZapSigType passing wrong reader for nested types** — MODULE_ZAPSIG
   should change `mdReader` for type tokens but preserve `outerMdReader` for type args.
   Fix: DecodeModuleZapSigType passes the target module as mdReader but keeps
   outerMdReader unchanged.
