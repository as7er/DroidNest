#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('DroidNest-package-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $workspace ('client space ' + [char]0x4e2d + [char]0x6587)
$allowed = @('DroidNest.exe', 'DroidNest.exe.config', 'Start-DroidNest.cmd', 'Check-Environment.ps1',
    'New-PortablePackage.ps1', 'tools/SignWebView.java', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
    'RELEASE-NOTES.md', 'CHANGELOG.md', 'VALIDATION.md')
$version = '0.0.0-ci.' + [Guid]::NewGuid().ToString('N')
$baseName = 'DroidNest-v' + $version + '-win-x64-client'
[IO.Directory]::CreateDirectory($client) | Out-Null
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
try {
    # Copy only the public inputs; never traverse an actual runtime or phone-data tree.
    foreach ($relative in $allowed) {
        $destination = Join-Path $client $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy((Join-Path $repo $relative), $destination)
    }
    # Synthetic excluded files prove the package is allowlisted, not merely a clean checkout.
    foreach ($relative in @('runtime/private.txt', 'data/phone.txt', 'logs/client.log', 'secrets/private.txt',
            'verification/private.txt', '.git/private.txt', 'tests/private.txt', 'testkey.pk8', 'adbkey')) {
        $destination = Join-Path $client $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::WriteAllText($destination, 'synthetic excluded fixture; no real secrets or phone data')
    }
    $packager = Join-Path $client 'New-PortablePackage.ps1'
    $rejected = $false
    try { & $packager -Mode Client -IncludeData -Version $version }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Client packaging accepted IncludeData.' }
    if (Test-Path -LiteralPath (Join-Path $client 'dist')) { throw 'Rejected data packaging created output.' }

    & $packager -Mode Client -Version $version
    $zipPath = Join-Path $client ('dist/' + $baseName + '.zip')
    $checksumPath = Join-Path $client ('dist/' + $baseName + '-SHA256SUMS.txt')
    $hash = (Get-Sha256 $zipPath).ToLowerInvariant()
    $checksum = [IO.File]::ReadAllText($checksumPath)
    if ($checksum.Trim() -cne ($hash + '  ' + $baseName + '.zip')) { throw 'Package checksum mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne $allowed.Count) { throw 'Unexpected package entry count.' }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $allowed -or -not $seen.Add($entry.FullName)) {
                throw ('Unexpected or duplicate package entry: ' + $entry.FullName)
            }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            $expected = Get-Sha256 (Join-Path $repo $entry.FullName)
            if ($actual -ne $expected) { throw ('Packaged bytes differ: ' + $entry.FullName) }
        }
    } finally { $zip.Dispose() }

    $rejected = $false
    try { & $packager -Mode Client -Version $version }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Packaging overwrote an existing output.' }
    if ((Get-Sha256 $zipPath).ToLowerInvariant() -ne $hash -or
        [IO.File]::ReadAllText($checksumPath) -cne $checksum) { throw 'Refused overwrite changed published output.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $client 'dist') -Force).Count -ne 2) {
        throw 'Packaging left unexpected output or staging files.'
    }
    Write-Output 'PASS: isolated client package allowlist, file bytes, checksum, data exclusion and overwrite refusal.'
} finally {
    # Only this invocation owns this unique directory; repository dist and user state are untouched.
    Remove-Item -LiteralPath $workspace -Recurse -Force
}
