# Terminal retained-memory regression

The old NuGet VtNetCore 1.0.30 allocates two compiled Regex instances in each
TerminalColor. ANSI true-color characters copy colors, retaining the regex
compiler graphs for every retained character. A bounded 2,000-line history
therefore does not imply a small memory footprint.

Use the pinned MIT source project `src/Terminal.VtNetCore`, not the old package.
Its 48 source files are hash-bound to upstream commit
`39237314c5e6e90be6a0a0cfa29e1f2caa8d1775`; the regexes are static readonly.
The old and repaired DLLs expose identical 1,017-entry public API inventories.
The source pin is an upstream revision, not a claim that the published NuGet
contains exactly the same source except for two lines.

TerminalView also clears formatted-text and brush caches immediately when its
session is changed or cleared. Rendering and ANSI parsing remain separate:
the collector parses in the background; the UI draws immutable latest frames.

## Offline acceptance

`src/Terminal.MemoryTests` accepts frozen `CoreReference` and `VtReference`
build properties. Run its EXE with a fresh output directory. `--baseline`
limits the old DLL to 40 lines; never run a large old-library load on production.
`--soak` exercises three consoles with 72,000 true-color lines for at least one
minute. There is no forced GC inside the sustained workload. Explicit GC is
used only in the separate retained-heap measurement phases, not in the product.

The original DLL retained approximately 40.3 MB after the same 40-line input;
the repaired DLL retained approximately 0.72 MB. The repaired 12,040-line run
retained approximately 11.5 MB with a 2,000-line history plus viewport. These
are process-local regression measurements, not guarantees for every workload.

The first test draft incorrectly expected italic spans; both old and pinned
upstream renderers explicitly report Italic=false. The original failed test
records are preserved. The corrected test checks the existing color, bold,
underline, CR, erase, cursor, and non-italic contract on both binaries.

Run `src/Desktop.Bench/Run.ps1` with an exact `VtReference` and frozen
`CoreReference`. It snapshots the product terminal/collector and validates
1,500/4,000/10,000 lines, UI heartbeat, scroll pause/resume, world isolation,
command routing, history limits, and cleared-view cache release.

For a UI-only release, Desktop supports `FrozenRuntimeDirectory` containing
approved Core and MapStudio DLLs. This avoids silently rebuilding and deploying
an unrelated Host/Core/map engine during a terminal repair. The default build
still uses the source project references.

Product runtime does not depend on PowerShell or shell scripts. Build drivers,
one-time delivery migration, and diagnostic probes are development/acceptance
tools only. A short idle observation is not proof against every long-run leak;
retain memory samples and distinguish actual remote UI verification from
game startup or real-player E2E, neither of which this repair requires.
