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
    foreach ($relative in $files) {
        if ($relative -eq 'external/MapStudio') { continue }
        $allowed = $relative -in $rootFiles -or $relative -match '^(src|docs|\.github)/'
        if ($relative.StartsWith('scripts/')) { $allowed = $relative.Substring(8) -in $scripts }
        if (!$allowed -or $relative -match '(?i)(^|/)(bin|obj|history|evidence|integrations|runtime|out)/|\.(dll|exe|pdb|zip|7z|wld|db|sqlite|log|pem|key|pfx|p12)(\.|$)|(^|/)\.env') {
            $problems.Add([pscustomobject]@{ Path=$relative; Reason='Forbidden publication path' }); continue
        }
        foreach ($hit in Select-String -LiteralPath (Join-Path $root $relative) -Pattern $patterns) {
            $problems.Add([pscustomobject]@{ Path=$relative; Line=$hit.LineNumber; Reason='Secret or personal local path pattern' })
        }
    }
    [pscustomobject]@{ IndexedFiles=$files.Count; Problems=$problems.Count; Findings=@($problems.ToArray()); Scope='Tracked-file allowlist + targeted secret/path scan; not exhaustive' }
    if ($problems.Count) { throw 'Publication boundary check failed.' }
} finally { Pop-Location }
