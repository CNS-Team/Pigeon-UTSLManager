#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][string]$ProbeOutput,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$Panorama
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageRoot)
$probeOutput = [IO.Path]::GetFullPath($ProbeOutput)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (!(Test-Path -LiteralPath (Join-Path $package 'UTSL服务器管理器.exe'))) { throw 'Packaged root EXE missing.' }
if (!(Test-Path -LiteralPath (Join-Path $probeOutput 'Manager.ConsoleProbe.exe'))) { throw 'Console fixture probe missing.' }
if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory exists; refusing to overwrite.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$fixtureMarker = Join-Path $package '.manager-ui-fixture'
$fixture = Join-Path $evidence 'fixture-package'
try {
    Set-Content -LiteralPath $fixtureMarker -Value 'LOCAL TEST FIXTURE ONLY' -Encoding utf8
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Get-ChildItem -LiteralPath $probeOutput -File | Copy-Item -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $probeOutput 'Manager.ConsoleProbe.exe') -Destination (Join-Path $fixture 'UnifierTSL.exe')
    New-Item -ItemType Directory -Path (Join-Path $fixture 'config') | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'config/config.json') -Value '{"launcher":{"listenPort":29989}}' -Encoding utf8
    $uiEvidence = Join-Path $evidence 'ui'
    $args = @('--ui-smoke', $uiEvidence, '--probe-package', $fixture, '--root', $package)
    if ($Panorama) { $args += @('--panorama', [IO.Path]::GetFullPath($Panorama)) }
    $launch = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'UTSL服务器管理器.exe'))
    $launch.UseShellExecute = $false
    foreach ($argument in $args) { $launch.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($launch)
    $process.WaitForExit()
    $resultPath = Join-Path $uiEvidence 'result.json'
    if (!(Test-Path -LiteralPath $resultPath)) { throw "Packaged UI smoke did not produce evidence (exit $($process.ExitCode))." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $failed = @($result.checks | Where-Object { !$_.pass })
    if ($process.ExitCode -ne 0 -or $result.failed -ne 0 -or $failed.Count -gt 0) { throw "Packaged GUI smoke failed: exit=$($process.ExitCode), result=$($result.passed)P/$($result.failed)F" }
} finally {
    if (Test-Path -LiteralPath $fixtureMarker) { Remove-Item -LiteralPath $fixtureMarker -Force }
}
$leftovers = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like (Join-Path $package '程序/host/*') })
if ($leftovers.Count -ne 0) { throw 'Fixture host process remains.' }
[pscustomobject]@{ Passed=$result.passed; Failed=$result.failed; ExitCode=$process.ExitCode; Evidence=$evidence; ProductHash=(Get-FileHash -LiteralPath (Join-Path $package 'UTSL服务器管理器.exe')).Hash; FixtureProcessesStopped=$true; Scope='packaged local GUI and probe servers only; no production servers or game clients' } | ConvertTo-Json -Depth 5
