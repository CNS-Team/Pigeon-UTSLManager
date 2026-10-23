#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][string]$ProbeOutput,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$Panorama,
    [string]$RuntimePluginV1,
    [string]$RuntimePluginV2,
    [switch]$SoftwareRendering
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageRoot)
$probeOutput = [IO.Path]::GetFullPath($ProbeOutput)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (!(Test-Path -LiteralPath (Join-Path $package 'UTSl-Manager.exe'))) { throw 'Packaged root EXE missing.' }
if (!(Test-Path -LiteralPath (Join-Path $probeOutput 'Manager.ConsoleProbe.exe'))) { throw 'Console fixture probe missing.' }
if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory exists; refusing to overwrite.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$manifestPath = Join-Path $package 'App/package-manifest.json'
$manifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (!$manifest.files -or $manifest.files.Count -eq 0) { throw 'Package manifest is empty.' }
foreach ($file in $manifest.files) {
    $path = Join-Path $package $file.path
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) {
        throw "Package differs from manifest before GUI acceptance: $($file.path)"
    }
}
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
    if ([bool]$RuntimePluginV1 -ne [bool]$RuntimePluginV2) { throw 'Both runtime fixture plugin versions are required.' }
    if ($RuntimePluginV1) { $args += @('--runtime-library-smoke','--runtime-plugin-v1',[IO.Path]::GetFullPath($RuntimePluginV1),'--runtime-plugin-v2',[IO.Path]::GetFullPath($RuntimePluginV2)) }
    if ($SoftwareRendering) { $args += '--software-rendering' }
    if ($Panorama) { $args += @('--panorama', [IO.Path]::GetFullPath($Panorama)) }
    $launch = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'UTSl-Manager.exe'))
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
$leftovers = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like (Join-Path $package 'App/host/*') })
if ($leftovers.Count -ne 0) { throw 'Fixture host process remains.' }
$afterChecks = @($manifest.files | ForEach-Object {
    $path = Join-Path $package $_.path
    @{ path=$_.path; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path).Hash;
       pass=(Get-Item -LiteralPath $path).Length -eq $_.bytes -and (Get-FileHash -LiteralPath $path).Hash -eq $_.sha256 }
})
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $manifestHash -or @($afterChecks | Where-Object { !$_.pass }).Count) {
    throw 'Package changed during GUI acceptance.'
}
@{ at=(Get-Date -Format o); package=$package; manifestSha256=$manifestHash;
   resultPath=$resultPath; resultSha256=(Get-FileHash -LiteralPath $resultPath).Hash;
   passed=$result.passed; failed=$result.failed; exitCode=$process.ExitCode;
   fixtureProcessesStopped=$true; files=$afterChecks } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $evidence 'gui-acceptance.json') -Encoding utf8
[pscustomobject]@{ Passed=$result.passed; Failed=$result.failed; ExitCode=$process.ExitCode; Evidence=$evidence; ProductHash=(Get-FileHash -LiteralPath (Join-Path $package 'UTSl-Manager.exe')).Hash; FixtureProcessesStopped=$true; Scope='packaged local GUI and probe servers only; no production servers or game clients' } | ConvertTo-Json -Depth 5
