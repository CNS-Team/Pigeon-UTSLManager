#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach($target in @(@{Project='Host';Out='host'},@{Project='Desktop';Out='manager'},@{Project='Probe';Out='probe'},@{Project='Tests';Out='tests'},@{Project='StartupTests';Out='startup-tests'})){
    & dotnet build (Join-Path $root "src/$($target.Project)/$($target.Project).csproj") -c Release -o (Join-Path $root "out/$($target.Out)") --nologo
    if($LASTEXITCODE -ne 0){throw "$($target.Project) build failed"}
}
