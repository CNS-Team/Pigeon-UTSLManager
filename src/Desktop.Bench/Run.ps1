#requires -Version 7.0
param(
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$OutputRoot,
    [string]$CoreReference,
    [string]$TerminalCandidate
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workspace = Split-Path (Split-Path $repo)
if (!$OutputRoot) { $OutputRoot = Join-Path $workspace '_tmp_build/utsl-manager-shared-runtime-20261023/console-decision' }
if (!$CoreReference) { $CoreReference = Join-Path $workspace '_tmp_build/utsl-manager-shared-runtime-20261023/ui-package-r3/App/manager/Pigeon.UTSLManager.Core.dll' }
$run = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $RunId
if (Test-Path -LiteralPath $run) { throw 'Run directory exists; choose a fresh RunId.' }
$snapshot = Join-Path $run 'source'
$product = Join-Path $snapshot 'Product'
$bench = Join-Path $snapshot 'Bench'
New-Item -ItemType Directory -Path $product,$bench,(Join-Path $snapshot 'Reference') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'src/Desktop/TerminalView.cs'),(Join-Path $repo 'src/Desktop/ConsoleCollector.cs') -Destination $product
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Desktop.Bench.csproj'),(Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'Run.ps1') -Destination $bench
Copy-Item -LiteralPath $CoreReference -Destination (Join-Path $snapshot 'Reference/Pigeon.UTSLManager.Core.dll')
$inputs = @(Join-Path $repo 'src/Desktop/TerminalView.cs'; Join-Path $repo 'src/Desktop/ConsoleCollector.cs'; Join-Path $PSScriptRoot 'Desktop.Bench.csproj'; Join-Path $PSScriptRoot 'Program.cs'; Join-Path $PSScriptRoot 'Run.ps1'; [IO.Path]::GetFullPath($CoreReference))
$manifest = foreach ($path in $inputs) { $file=Get-Item -LiteralPath $path; [pscustomobject]@{Path=$file.FullName;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $path).Hash} }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'source-manifest.json') -Encoding utf8
if ($TerminalCandidate) {
    $file=Get-Item -LiteralPath $TerminalCandidate
    [pscustomobject]@{Path=$file.FullName;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $TerminalCandidate).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'candidate-manifest.json')
}
if ($TerminalCandidate) {
    Copy-Item -LiteralPath (Join-Path $product 'TerminalView.cs') -Destination (Join-Path $product 'TerminalView.cs.baseline')
    Copy-Item -LiteralPath $TerminalCandidate -Destination (Join-Path $product 'TerminalView.cs')
}
# A single measurement callback after the real DrawText loop; no parser/render algorithm changes.
$terminalPath = Join-Path $product 'TerminalView.cs'
$terminal = Get-Content -LiteralPath $terminalPath -Raw
Copy-Item -LiteralPath $terminalPath -Destination (Join-Path $product 'TerminalView.cs.uninstrumented')
$anchor = "        }`n    }`n}"
$normalized = $terminal.Replace("`r`n", "`n")
$index = $normalized.LastIndexOf($anchor, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'TerminalView instrumentation anchor changed; inspect the source.' }
$instrumented = $normalized.Substring(0,$index) + $normalized.Substring($index).Replace($anchor,"        }`n        ConsoleBenchHooks.OnRendered(this, frame);`n    }`n}")
$instrumented = $instrumented.Replace('public void Apply(LogPage? page) {', 'public void Apply(LogPage? page) { long benchApplyStart = System.Diagnostics.Stopwatch.GetTimestamp();')
$instrumented = $instrumented.Replace('if (!changed) return;', 'if (!changed) return; long benchParseDone = System.Diagnostics.Stopwatch.GetTimestamp();')
$instrumented = $instrumented.Replace('Volatile.Write(ref plainText,', 'long benchFrameDone = System.Diagnostics.Stopwatch.GetTimestamp(); Volatile.Write(ref plainText,')
$instrumented = $instrumented.Replace('Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines));', 'Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines)); ConsoleBenchHooks.OnApplied(this, benchApplyStart, benchParseDone, benchFrameDone);')
$instrumented = $instrumented.Replace('Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines, Math.Max(0, terminal.ViewPort.TopRow - firstRow)));', 'Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines, Math.Max(0, terminal.ViewPort.TopRow - firstRow))); ConsoleBenchHooks.OnApplied(this, benchApplyStart, benchParseDone, benchFrameDone);')
$instrumented = $instrumented.Replace('Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines, Math.Max(0, terminal.ViewPort.TopRow - firstRow), viewport.Version));', 'Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines, Math.Max(0, terminal.ViewPort.TopRow - firstRow), viewport.Version)); ConsoleBenchHooks.OnApplied(this, benchApplyStart, benchParseDone, benchFrameDone);')
[IO.File]::WriteAllText($terminalPath,$instrumented,[Text.UTF8Encoding]::new($false))
$variant = if ($TerminalCandidate) { 'offline-candidate' } else { 'frozen-baseline' }
[pscustomobject]@{variant=$variant;terminalCandidate=$TerminalCandidate;renderInstrumentation='one callback after the existing DrawText loop and Apply duration timestamps';productionTouched=$false;source=$snapshot} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'run-manifest.json')
$core = Join-Path $snapshot 'Reference/Pigeon.UTSLManager.Core.dll'
$project = Join-Path $bench 'Desktop.Bench.csproj'
& dotnet build $project -c Release --artifacts-path (Join-Path $run 'artifacts') -o (Join-Path $run 'bin') "-p:CoreReference=$core" --nologo *> (Join-Path $run 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $run 'build.log') -Tail 35; exit $LASTEXITCODE }
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $run 'bin/Pigeon.ConsoleDecision.Bench.exe'))
$start.UseShellExecute=$false
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
$start.ArgumentList.Add((Join-Path $run 'evidence')); $start.ArgumentList.Add($variant)
$process=[Diagnostics.Process]::Start($start)
$outTask=$process.StandardOutput.ReadToEndAsync(); $errTask=$process.StandardError.ReadToEndAsync()
$process.WaitForExit()
[IO.File]::WriteAllText((Join-Path $run 'run.log'),$outTask.GetAwaiter().GetResult())
[IO.File]::WriteAllText((Join-Path $run 'stderr.log'),$errTask.GetAwaiter().GetResult())
$result=Join-Path $run 'evidence/result.json'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { Get-Content -LiteralPath (Join-Path $run 'stderr.log'); throw 'Bench did not write result.json' }
exit $process.ExitCode
