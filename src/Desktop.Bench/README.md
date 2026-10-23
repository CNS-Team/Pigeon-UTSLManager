# Offline TerminalView decision bench

This is a development and evidence tool. It is not referenced by Desktop, Host, Build.ps1 or Package.ps1 and is not a runtime dependency. No BAT, CMD or PowerShell participates in manager functionality.

Run from PowerShell 7 with .NET 10 installed:

```powershell
./src/Desktop.Bench/Run.ps1 -RunId fresh-baseline
```

The runner creates a private source snapshot under `_tmp_build/utsl-manager-shared-runtime-20261023/console-decision`, copies the real TerminalView and ConsoleCollector, references a frozen Core DLL, builds and opens an actual software-rendered Avalonia window. Existing output directories are refused. Product source and old evidence are preserved. Supply `-CoreReference <dll>` or `-OutputRoot <directory>` to override the default local development paths.

To test a private C# candidate without replacing the product file:

```powershell
./src/Desktop.Bench/Run.ps1 -RunId fresh-candidate -TerminalCandidate '<private-candidate-directory>/TerminalView.cs'
```

Each run preserves 1,500 / 4,000 / 10,000 lines of ANSI/Chinese help-equivalent output, three rounds each, raw 10ms heartbeat timestamps, completed draw-loop counts, newly published frame counts, history sizes, scrolling and host/world isolation checks. Counters are injected only into the private source copy; they do not count physical display presents. Screenshots are excluded from the measured workload interval.

Exit code 1 means an actual failed check. In particular, the existing 1.5k `<1s` acceptance line remains enforced and failures are not converted to warnings. The fixture is a local named-pipe replay backend, not an actual Host/server/game E2E. Real `/help` is `UNVERIFIED` by this bench.

The final decision, preserved failed runs, C# scroll-anchor candidate, narrow diff, Desktop candidate build and hashes are documented in the run root's `DECISION.md` and `freeze-manifest.json`.
