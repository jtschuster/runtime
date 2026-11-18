# Runtime Tests

This folder is the **CoreCLR / runtime test tree**. Tests here exercise the
runtime itself (JIT, GC, EE, interop, threading, tracing, …) rather than the
managed libraries — for those, see `src/libraries/<Name>/tests/`.

Tests are driven by `src/tests/build.sh` and `src/tests/run.sh`, not by
`dotnet test`. The wrong tool will silently report "0 test projects" or fail
to find a testhost.

## Building and running

For up-front baseline build requirements (mandatory under CCA,
probe-and-fall-back under CLI), see
[`.github/copilot-instructions.md`](/.github/copilot-instructions.md).

- [Testing CoreCLR](/docs/workflow/testing/coreclr/testing.md) — the
  authoritative guide for this tree, including `Core_Root` setup, single-test
  builds, and `corerun`
- [Test configuration options](/docs/workflow/testing/coreclr/test-configuration.md)
- [`<RequiresProcessIsolation>` tests](/docs/workflow/testing/coreclr/requiresprocessisolation.md)
- [Disasm checks](/docs/workflow/testing/coreclr/disasm-checks.md)
- [Unix test instructions](/docs/workflow/testing/coreclr/unix-test-instructions.md) · [Windows test instructions](/docs/workflow/testing/coreclr/windows-test-instructions.md)
- [Running ARM32 tests](/docs/workflow/testing/coreclr/running-arm32-tests.md)
- [GC stress runs](/docs/workflow/testing/coreclr/gc-stress-run-readme.md)

Run `src/tests/build.sh -h` for the full flag list. A few commonly-missed
flags:

| Flag | Description |
|------|-------------|
| `-Test <path>` | Build one project |
| `-Tree <path>` | Build a subtree recursively |
| `-priority1` (`-Priority 1` on Windows) | Required for tests with `<CLRTestPriority>1</CLRTestPriority>`; without it the build silently reports "0 test projects" |
| `-GenerateLayoutOnly` | Generate `Core_Root` only (required before running individual tests with `corerun`) |

Many subdirectories under `src/tests/` have their own `README.md` with
area-specific authoring and run guidance — read them before making changes.

## Crossgen2 test caching

`RunCrossgen2Tests` never modifies the IL in a test's directory. Each run recreates
`readytorun/` as a copy of the test directory (without wrapper scripts and test markers),
compiles the test assemblies into it, and runs the test from there. Whole-program
composite mode uses `readytorun-wholeprogram/` instead, which also holds a copy of the
`Core_Root` assemblies so that the test loads the compiled CoreLib. Out-of-process tests
are still launched from their wrappers in the original directory. Concurrent runs of the
same test directory are not supported.

The test launcher calls `RunReadyToRunCompiler` to cache PE outputs. Changes to Crossgen2, the runtime
and references in `Core_Root`, test inputs, compiler arguments, or runtime/JIT environment
settings invalidate the cache. Refresh `Core_Root` after rebuilding the product; no
cache directories need to be deleted. If an older wrapper overwrote the original IL,
clean and rebuild the affected tests once before using the new wrapper.

Caching is enabled by default. Set `DOTNET_CROSSGEN2_CACHE=false` to compile
without caching, or `DOTNET_CROSSGEN2_CACHE_PATH` to override the user cache
directory. Custom compiler arguments, symbol generation, synthesized profiles, and non-PE outputs
bypass caching and compile on every run. R2RDump and PDB validation remain outside
the compiler cache. The task host is published to `Core_Root/crossgen2-test` as a self-contained
CoreCLR app for the platform that runs Crossgen2, so no SDK or installed `dotnet` runtime
is needed to run it.
