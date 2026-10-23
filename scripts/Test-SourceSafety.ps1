#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $files = @(git -c core.quotepath=false ls-files --cached)
    if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw 'No indexed source files to verify.' }
    $rootFiles = @('.gitattributes','.gitignore','.gitmodules','Directory.Build.props','LICENSE','README.md','THIRD-PARTY-NOTICES.md')
    $scripts = @('Build.ps1','Package.ps1','Test-SourceSafety.ps1','Test-DesktopPackage.ps1')
    $problems = [Collections.Generic.List[object]]::new()
    $patterns = @(
        '-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----',
        ('gh'+'[pousr]_[A-Za-z0-9]{24,}'),
        ('github'+'_pat_[A-Za-z0-9_]{30,}'),
        '(?i)(?:password|api[_-]?key|access[_-]?token)\s*[:=]\s*["''][A-Za-z0-9_+/-]{16,}["'']',
        '(?i)[A-Z]:[\\/](?:Users[\\/][^\\/\s"'']+|59934|流光核心源码)[\\/]'
    )
    function Test-BrandingImage([string]$Path) {
        $item = Get-Item -LiteralPath $Path
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 4MB) { return $false }
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ([IO.Path]::GetExtension($Path) -ieq '.png') {
            if ($bytes.Length -lt 33 -or [Convert]::ToHexString($bytes, 0, 16) -ne '89504E470D0A1A0A0000000D49484452') { return $false }
            $width = [uint32]$bytes[16] * 16777216 + [uint32]$bytes[17] * 65536 + [uint32]$bytes[18] * 256 + $bytes[19]
            $height = [uint32]$bytes[20] * 16777216 + [uint32]$bytes[21] * 65536 + [uint32]$bytes[22] * 256 + $bytes[23]
            return $width -gt 0 -and $height -gt 0 -and $width -le 4096 -and $height -le 4096
        }
        if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) { return $false }
        $count = [BitConverter]::ToUInt16($bytes, 4)
        $directoryEnd = 6 + 16 * $count
        if ($count -eq 0 -or $directoryEnd -gt $bytes.Length) { return $false }
        for ($index = 0; $index -lt $count; $index++) {
            $entry = 6 + 16 * $index
            $length = [uint64][BitConverter]::ToUInt32($bytes, $entry + 8)
            $offset = [uint64][BitConverter]::ToUInt32($bytes, $entry + 12)
            if ($length -lt 40 -or $offset -lt $directoryEnd -or $offset + $length -gt $bytes.Length) { return $false }
            $pngFrame = [Convert]::ToHexString($bytes, [int]$offset, 8) -eq '89504E470D0A1A0A'
            if (!$pngFrame -and [BitConverter]::ToUInt32($bytes, [int]$offset) -ne 40) { return $false }
        }
        return $true
    }
    foreach ($relative in $files) {
        if ($relative -eq 'external/MapStudio') { continue }
        $brandingImage = $relative -cmatch '^assets/branding/[^/]+\.(png|ico)$'
        $allowed = $relative -in $rootFiles -or $relative -match '^(src|docs|\.github)/'
        if ($brandingImage) { $allowed = $true }
        if ($relative.StartsWith('scripts/')) { $allowed = $relative.Substring(8) -in $scripts }
        if (!$allowed -or $relative -match '(?i)(^|/)(bin|obj|history|evidence|integrations|runtime|out)/|\.(dll|exe|pdb|zip|7z|wld|db|sqlite|log|pem|key|pfx|p12)(\.|$)|(^|/)\.env') {
            $problems.Add([pscustomobject]@{ Path=$relative; Reason='Forbidden publication path' }); continue
        }
        if ($brandingImage) {
            if (!(Test-BrandingImage (Join-Path $root $relative))) { $problems.Add([pscustomobject]@{ Path=$relative; Reason='Invalid or oversized branding image' }) }
            continue
        }
        foreach ($hit in Select-String -LiteralPath (Join-Path $root $relative) -Pattern $patterns) {
            $problems.Add([pscustomobject]@{ Path=$relative; Line=$hit.LineNumber; Reason='Secret or personal local path pattern' })
        }
    }
    # Runtime features are implemented in C# / Windows APIs. These checks cover
    # product sources (including the pinned map generator), not development PS.
    $productRoots = @('src/Core','src/Host','src/Desktop','src/RuntimeTools',
        'external/MapStudio/src/Studio','external/MapStudio/src/Generator','external/MapStudio/src/NativeWorker')
    $runtimeFiles = @($productRoots | ForEach-Object {
        Get-ChildItem -LiteralPath (Join-Path $root $_) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    })
    foreach($file in $runtimeFiles){
        $relative = [IO.Path]::GetRelativePath($root,$file.FullName)
        if($file.Extension -in '.bat','.cmd','.ps1','.psm1','.psd1','.vbs','.vbe','.wsf','.hta','.js','.cjs','.mjs','.sh','.py'){
            $problems.Add([pscustomobject]@{Path=$relative;Reason='Runtime script is forbidden'})
        }
        if($file.Extension -eq '.cs'){
            $shells = '(?i)["''](?:[^"''\r\n]*[\\/])?(?:cmd|powershell|pwsh|wscript|cscript|mshta|bash|sh)(?:\.exe)?["'']|["''][^"''\r\n]*\.(?:bat|cmd|ps1|vbs|wsf|hta|sh)["'']'
            foreach($hit in Select-String -LiteralPath $file.FullName -Pattern $shells){
                $problems.Add([pscustomobject]@{Path=$relative;Line=$hit.LineNumber;Reason='Runtime shell/script literal requires removal'})
            }
        }
    }
    [pscustomobject]@{ IndexedFiles=$files.Count; RuntimeFiles=$runtimeFiles.Count; Problems=$problems.Count; Findings=@($problems.ToArray()); Scope='Tracked-file allowlist, targeted secret scan, runtime script/shell guard; not exhaustive' }
    if ($problems.Count) { throw 'Publication boundary check failed.' }
} finally { Pop-Location }
