#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    $tracked = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard -- '*.ps1' '*.psm1' '*.psd1')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source PowerShell files.' }
    if ($tracked.Count -eq 0) { throw 'No source PowerShell files found.' }
    $failures = @()
    foreach ($relative in $tracked) {
        $tokens = $null
        $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $relative), [ref]$tokens, [ref]$errors)
        foreach ($error in $errors) {
            $failures += '{0}:{1}: {2}' -f $relative, $error.Extent.StartLineNumber, $error.Message
        }
    }
    if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
    Write-Output ('PASS: parsed {0} source PowerShell scripts.' -f $tracked.Count)
} finally {
    Pop-Location
}
