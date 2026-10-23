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
foreach($folder in @('插件库','服务器','地图','备份','数据','程序/manager','程序/host','程序/mapstudio/app','程序/mapstudio/worker','程序/mapstudio/engine','程序/mapstudio/lib/TEdit','程序/许可证')){New-Item -ItemType Directory -Path (Join-Path $destination $folder)|Out-Null}
foreach($pair in @(@('out/manager','程序/manager'),@('out/host','程序/host'),@('out/map-app','程序/mapstudio/app'),@('out/map-worker','程序/mapstudio/worker'))){Get-ChildItem -LiteralPath (Join-Path $root $pair[0])|Copy-Item -Destination (Join-Path $destination $pair[1]) -Recurse}
$lock=Get-Content (Join-Path $map 'dependency-manifest.json') -Raw|ConvertFrom-Json
foreach($file in @($lock.engine)+@($lock.tedit)){$source=Join-Path $map $file.path;$target=Join-Path $destination ('程序/mapstudio/'+$file.path);Copy-Item -LiteralPath $source -Destination $target;if((Get-FileHash -LiteralPath $target).Hash -ne $file.sha256){throw 'Packaged dependency hash mismatch'}}
Copy-Item -LiteralPath (Join-Path $map 'LICENSE') -Destination (Join-Path $destination '程序/许可证/MapStudio-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $map 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $destination '程序/许可证/MapStudio-THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $destination '程序/许可证/Manager-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $destination '程序/许可证/THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $destination '使用说明.md')
$dotnet=Split-Path (Get-Command dotnet).Source
$sdk=(& dotnet --list-sdks|Select-String '^10\.'|Select-Object -Last 1).ToString().Split(' ')[0]
Add-Type -Path (Join-Path $dotnet "sdk/$sdk/Microsoft.NET.HostModel.dll")
$pack=Get-ChildItem (Join-Path $dotnet 'packs/Microsoft.NETCore.App.Host.win-x64') -Directory|Where-Object Name -like '10.*'|Sort-Object {[version]$_.Name}|Select-Object -Last 1
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost((Join-Path $pack.FullName 'runtimes/win-x64/native/apphost.exe'),(Join-Path $destination 'UTSL服务器管理器.exe'),'程序/manager/Pigeon.UTSLManager.dll',$true,(Join-Path $destination '程序/manager/Pigeon.UTSLManager.dll'),$false,$false,$null)
$facts=@(Get-ChildItem -LiteralPath (Join-Path $destination '程序') -Recurse -File|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($destination,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})+@([ordered]@{path='UTSL服务器管理器.exe';bytes=(Get-Item -LiteralPath (Join-Path $destination 'UTSL服务器管理器.exe')).Length;sha256=(Get-FileHash -LiteralPath (Join-Path $destination 'UTSL服务器管理器.exe')).Hash})
[ordered]@{at=(Get-Date -Format o);version='0.1.0';source=(git -C $root rev-parse HEAD 2>$null);mapSource=(git -C $map rev-parse HEAD);scope='local package; no servers imported or started';files=@($facts)}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $destination '程序/package-manifest.json') -Encoding utf8
Get-Item -LiteralPath (Join-Path $destination 'UTSL服务器管理器.exe')|Select-Object FullName,Length
