#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach($target in @(@{Project='Host';Out='host'},@{Project='Desktop';Out='manager'},@{Project='Probe';Out='probe'},@{Project='Tests';Out='tests'},@{Project='StartupTests';Out='startup-tests'},@{Project='RuntimeTools';Out='runtime-tools'},@{Project='RuntimeTests';Out='runtime-tests'},@{Project='RuntimeTests/Fixture';File='RuntimeFixture';Out='runtime-fixture'},@{Project='RuntimeTests/PluginV1';File='RuntimePluginV1';Out='runtime-plugin-v1'},@{Project='RuntimeTests/PluginV2';File='RuntimePluginV2';Out='runtime-plugin-v2'})){
    $projectName=if($target.File){$target.File}else{$target.Project}
    & dotnet build (Join-Path $root "src/$($target.Project)/$projectName.csproj") -c Release -o (Join-Path $root "out/$($target.Out)") --nologo
    if($LASTEXITCODE -ne 0){throw "$($target.Project) build failed"}
}
