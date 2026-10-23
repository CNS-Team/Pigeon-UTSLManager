#requires -Version 7.0
param(
    [string]$ArtifactRoot,
    [string]$ActualCore,
    [string[]]$ProviderSources = @(),
    [ValidateSet('all','runtime','boundary','host','module')][string]$Scope = 'all',
    [string]$RunName = ('run-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workspace=Split-Path -Parent (Split-Path -Parent $repo)
if(!$ArtifactRoot){$ArtifactRoot=Join-Path $workspace '_tmp_build/utsl-manager-shared-runtime-20261023'}
if(!$ActualCore){$ActualCore=Join-Path $workspace '_tmp_build/utsl-manager-production-20261023-152156/sequential-start/core-Game/build1'}
if($RunName -match '[^A-Za-z0-9._-]' -or $RunName -in '.','..'){throw 'RunName must be a directory name'}
$run=Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) $RunName
if(Test-Path -LiteralPath $run){throw 'Run directory already exists'}
New-Item -ItemType Directory -Path $run | Out-Null
$stage='initialize'
trap {
    try {
        [ordered]@{stage=$stage;error=$_.Exception.Message;stack=$_.ScriptStackTrace;at=(Get-Date -Format o);status='FAILED'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'failure.json') -Encoding utf8
    } catch { }
    throw $_
}
$build=Join-Path $run 'build'
$nuget=Join-Path $run 'NuGet.Offline.Config'
[IO.File]::WriteAllText($nuget,'<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/></packageSources></configuration>')
$projects=@(
    'src/RuntimeTests/Fixture/RuntimeFixture.csproj',
    'src/RuntimeTests/PluginV1/RuntimePluginV1.csproj',
    'src/RuntimeTests/PluginV2/RuntimePluginV2.csproj',
    'src/RuntimeTests/ModuleSupport/ModuleSupport.csproj',
    'src/RuntimeTests/ModuleCore/ModuleCore.csproj',
    'src/RuntimeTests/ModuleChild/ModuleChild.csproj',
    'src/RuntimeTests/RuntimeModuleTests/RuntimeModuleTests.csproj',
    'src/RuntimeTests/RuntimeTests.csproj',
    'src/Host/Host.csproj'
)
function SourceSnapshot {
    $sourceInputs=@(Get-ChildItem -LiteralPath (Join-Path $repo 'src/Core'),(Join-Path $repo 'src/Host'),$PSScriptRoot -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.ps1' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    $sourceInputs+=Get-Item -LiteralPath (Join-Path $repo 'Directory.Build.props')
    @($sourceInputs |
        Sort-Object FullName | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
}
$sourcesBefore=SourceSnapshot
$sourcesBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'source-manifest-before.json') -Encoding utf8
foreach($project in $projects) {
    $path=Join-Path $repo $project
    $name=[IO.Path]::GetFileNameWithoutExtension($path)
    $extra=@("-p:RuntimeCoreDirectory=$ActualCore",'-p:RestorePackagesWithLockFile=true',('-p:NuGetLockFilePath='+$build+'/locks/$(MSBuildProjectName).packages.lock.json'))
    $stage="restore $name"
    & dotnet restore $path --artifacts-path $build --configfile $nuget @extra --nologo 2>&1 | Tee-Object -FilePath (Join-Path $run ($name+'-restore.log'))
    if($LASTEXITCODE -ne 0){throw "$name offline restore failed; evidence: $run"}
    $stage="build $name"
    & dotnet build $path -c Release --no-restore --artifacts-path $build @extra --nologo 2>&1 | Tee-Object -FilePath (Join-Path $run ($name+'-build.log'))
    if($LASTEXITCODE -ne 0){throw "$name build failed; evidence: $run"}
}
function Built([string]$Project,[string]$File) {
    $path=Join-Path $build "bin/$Project/release/$File"
    if(!(Test-Path -LiteralPath $path)){throw "Missing built fixture: $path"}
    return $path
}
$runner=Built 'RuntimeTests' 'Manager.RuntimeTests.exe'
$hostExe=Built 'Host' 'Pigeon.UTSLManager.Host.exe'
$fixture=Split-Path -Parent (Built 'RuntimeFixture' 'Manager.RuntimeFixture.exe')
$v1=Built 'RuntimePluginV1' 'RuntimeFixture.Plugin.dll'
$v2=Built 'RuntimePluginV2' 'RuntimeFixture.Plugin.dll'
$moduleTester=Built 'RuntimeModuleTests' 'Manager.RuntimeModuleTests.exe'
$coreModule=Built 'ModuleCore' 'RuntimeFixture.CoreModule.dll'
$childModule=Built 'ModuleChild' 'RuntimeFixture.ChildModule.dll'
$stage='bind release binaries'
$managerCore=Built 'Core' 'Pigeon.UTSLManager.Core.dll'
$hostCore=Built 'Host' 'Pigeon.UTSLManager.Core.dll'
$runnerCore=Built 'RuntimeTests' 'Pigeon.UTSLManager.Core.dll'
$coreHash=(Get-FileHash -LiteralPath $managerCore -Algorithm SHA256).Hash
if((Get-FileHash -LiteralPath $hostCore -Algorithm SHA256).Hash -ne $coreHash -or (Get-FileHash -LiteralPath $runnerCore -Algorithm SHA256).Hash -ne $coreHash){throw 'Host/runner/Core Release DLL hashes differ'}
@($runner,$hostExe,(Built 'Host' 'Pigeon.UTSLManager.Host.dll'),$managerCore,$hostCore,$runnerCore,$moduleTester,$coreModule,$childModule,(Built 'ModuleSupport' 'RuntimeFixture.ModuleSupport.dll'),(Join-Path $ActualCore 'UnifierTSL.dll')) |
    ForEach-Object { [ordered]@{path=$_;bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'binary-binding.json') -Encoding utf8
$sourcesBuilt=SourceSnapshot
$sourcesBuilt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'source-manifest-built.json') -Encoding utf8
if(($sourcesBefore | ConvertTo-Json -Compress -Depth 4) -cne ($sourcesBuilt | ConvertTo-Json -Compress -Depth 4)){throw 'Sources changed during build; run preserved, refusing mixed-source tests'}
$evidence=Join-Path $run 'evidence'
$stage='execute fixtures'
$priorScope=$env:RUNTIME_TEST_SCOPE
try {
    $env:RUNTIME_TEST_SCOPE=$Scope
    & $runner $evidence $hostExe $fixture $v1 $v2 $ActualCore $moduleTester $coreModule $childModule @ProviderSources 2>&1 | Tee-Object -FilePath (Join-Path $run 'test.log')
    $testExit=$LASTEXITCODE
} finally {
    if($null -eq $priorScope){Remove-Item Env:RUNTIME_TEST_SCOPE -ErrorAction SilentlyContinue}else{$env:RUNTIME_TEST_SCOPE=$priorScope}
}
if(Test-Path -LiteralPath (Join-Path $evidence 'result.json')) {
    [ordered]@{result=(Join-Path $evidence 'result.json');sha256=(Get-FileHash -LiteralPath (Join-Path $evidence 'result.json') -Algorithm SHA256).Hash;exitCode=$testExit} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'RESULT-LOCATION.json') -Encoding utf8
}
$sourcesAfter=SourceSnapshot
$sourcesAfter | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'source-manifest-after.json') -Encoding utf8
$sourceStable=($sourcesBefore | ConvertTo-Json -Compress -Depth 4) -ceq ($sourcesAfter | ConvertTo-Json -Compress -Depth 4)
[ordered]@{run=$run;testExit=$testExit;result=(Join-Path $evidence 'result.json');testScope=$Scope;providerSources=$ProviderSources;sourceStable=$sourceStable;configuration='Release';scope='Offline fixture processes only; no SSH, no production, no full game E2E'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
Write-Host "RuntimeTests artifacts: $run"
if($testExit -ne 0){throw "RuntimeTests failed ($testExit); inspect $run"}
if(!$sourceStable){throw 'Sources changed during execution; result is not bound to one frozen source set'}
