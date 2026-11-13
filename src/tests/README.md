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

`RunCrossgen2Tests` preserves original IL in `IL-CG2` and uses `CachedToolTask`
to cache PE compilation outputs. Changes to Crossgen2, the runtime and references
in `Core_Root`, test inputs, compiler arguments, or runtime/JIT environment settings
invalidate the cache. Refresh `Core_Root` after rebuilding the product; deleting
`IL-CG2` is not necessary. Test builds restore the original IL before copying new
assemblies.

Caching is enabled by default. Set `DOTNET_CROSSGEN2_CACHE=false` to compile
without caching, or `DOTNET_CROSSGEN2_CACHE_PATH` to override the user cache
directory. Custom compiler arguments, symbol generation, synthesized profiles, and non-PE outputs
bypass caching and compile on every run. R2RDump and PDB validation remain outside
the compiler cache. The task host is published to `Core_Root/crossgen2-test` as a NativeAOT
executable (single-file where NativeAOT is unsupported), so the test machine needs neither
an SDK nor a `dotnet` runtime to run it.
