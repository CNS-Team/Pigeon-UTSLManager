#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$Destination,[Parameter(Mandatory=$true)][string]$MapDependencyRoot)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $destination){throw 'Destination already exists; do not overwrite user packages/data.'}
$map=Join-Path $root 'external/MapStudio'
& (Join-Path $map 'scripts/Import-Dependencies.ps1') -EngineSource (Join-Path $MapDependencyRoot 'engine') -TEditSource (Join-Path $MapDependencyRoot 'lib/TEdit')|Format-List
& (Join-Path $PSScriptRoot 'Build.ps1')
& dotnet build (Join-Path $map 'src/Generator/Generator.csproj') -c Release -o (Join-Path $root 'out/map-app') --nologo
if($LASTEXITCODE -ne 0){throw 'Map generator build failed'}
& dotnet build (Join-Path $map 'src/NativeWorker/NativeWorker.csproj') -c Release -o (Join-Path $root 'out/map-worker') --nologo
if($LASTEXITCODE -ne 0){throw 'Map worker build failed'}
New-Item -ItemType Directory -Path $destination|Out-Null
foreach($folder in @('Core/Engines','Core/Runtime','Plugins','Server/World','Backups','Data','App/manager','App/host','App/mapstudio/app','App/mapstudio/worker','App/mapstudio/engine','App/mapstudio/lib/TEdit','App/licenses')){New-Item -ItemType Directory -Path (Join-Path $destination $folder) -Force|Out-Null}
foreach($pair in @(@('out/manager','App/manager'),@('out/host','App/host'),@('out/map-app','App/mapstudio/app'),@('out/map-worker','App/mapstudio/worker'))){Get-ChildItem -LiteralPath (Join-Path $root $pair[0])|Copy-Item -Destination (Join-Path $destination $pair[1]) -Recurse}
$lock=Get-Content (Join-Path $map 'dependency-manifest.json') -Raw|ConvertFrom-Json
foreach($file in @($lock.engine)+@($lock.tedit)){$source=Join-Path $map $file.path;$target=Join-Path $destination ('App/mapstudio/'+$file.path);Copy-Item -LiteralPath $source -Destination $target;if((Get-FileHash -LiteralPath $target).Hash -ne $file.sha256){throw 'Packaged dependency hash mismatch'}}
Copy-Item -LiteralPath (Join-Path $map 'LICENSE') -Destination (Join-Path $destination 'App/licenses/MapStudio-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $map 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $destination 'App/licenses/MapStudio-THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $destination 'App/licenses/Manager-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $destination 'App/licenses/THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $destination 'README.md')
$dotnet=Split-Path (Get-Command dotnet).Source
$sdk=(& dotnet --list-sdks|Select-String '^10\.'|Select-Object -Last 1).ToString().Split(' ')[0]
Add-Type -Path (Join-Path $dotnet "sdk/$sdk/Microsoft.NET.HostModel.dll")
$pack=Get-ChildItem (Join-Path $dotnet 'packs/Microsoft.NETCore.App.Host.win-x64') -Directory|Where-Object Name -like '10.*'|Sort-Object {[version]$_.Name}|Select-Object -Last 1
$managerAssembly=Join-Path $destination 'App/manager/Pigeon.UTSLManager.dll'
# Copy the manager assembly's Win32 resources, including its ApplicationIcon, to the root apphost.
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost((Join-Path $pack.FullName 'runtimes/win-x64/native/apphost.exe'),(Join-Path $destination 'UTSl-Manager.exe'),'App/manager/Pigeon.UTSLManager.dll',$true,$managerAssembly,$false,$false,$null)
$forbidden = @(Get-ChildItem -LiteralPath (Join-Path $destination 'App') -Recurse -File | Where-Object {
    $_.Extension -in '.bat','.cmd','.ps1','.psm1','.psd1','.vbs','.vbe','.wsf','.hta','.js','.cjs','.mjs','.sh','.py' -or
    $_.Name -match '^(Manager\.(Tests|RuntimeTests|StartupTests|ConsoleProbe|RuntimeFixture)|Pigeon\.(Terminal\.MemoryTests|ConsoleDecision\.Bench))\.'
})
if($forbidden.Count){throw ('Runtime package must contain product code only, no scripts/test tools: '+($forbidden.Name -join ', '))}
$facts=@(Get-ChildItem -LiteralPath (Join-Path $destination 'App') -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($destination,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})+@([ordered]@{path='UTSl-Manager.exe';bytes=(Get-Item -LiteralPath (Join-Path $destination 'UTSl-Manager.exe')).Length;sha256=(Get-FileHash -LiteralPath (Join-Path $destination 'UTSl-Manager.exe')).Hash})
[ordered]@{at=(Get-Date -Format o);version='0.2.0';source=(git -C $root rev-parse HEAD 2>$null);mapSource=(git -C $map rev-parse HEAD);scope='local package; no servers imported or started';files=@($facts)}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $destination 'App/package-manifest.json') -Encoding utf8
Get-Item -LiteralPath (Join-Path $destination 'UTSl-Manager.exe')|Select-Object FullName,Length
