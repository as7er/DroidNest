#requires -Version 5.1
<#
.SYNOPSIS
Creates an allowlisted client ZIP, or a private local-runtime migration ZIP.
.DESCRIPTION
The default Client mode contains no Android runtime, phone data, logs, or keys.
Local mode reads installed runtime from %LOCALAPPDATA%\DroidNest, requires the
phone to be stopped, and NEVER produces a public-release artifact. Phone data
(including its Android identity/ADB keys) requires explicit -IncludeData.
The extracted local package is imported by the consent-driven client migration.

Outputs dist/DroidNest-v<VERSION>-win-x64-client.zip and a matching
-SHA256SUMS.txt sidecar. Local names contain LOCAL-ONLY and optionally with-data.
ZIP entries are rooted directly at the extraction directory. Existing outputs
are never overwritten. .NET ZipArchive streams files and supports ZIP64, unlike
Compress-Archive's large-file limitation. No build or download is performed.
.EXAMPLE
.\New-PortablePackage.ps1 -Version 0.2.0
.EXAMPLE
.\New-PortablePackage.ps1 -Mode Local -IncludeData
#>
[CmdletBinding()]
param(
    [ValidateLength(5, 64)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version = '0.2.0',

    [ValidateSet('Client', 'Local')]
    [string]$Mode = 'Client',

    [switch]$IncludeData
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ($IncludeData -and $Mode -ne 'Local') {
    throw '-IncludeData is permitted only with -Mode Local. Client releases never contain phone data.'
}

$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([char[]]'\/')
$sourcePrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
$stateRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'DroidNest')).TrimEnd([char[]]'\/')
$statePrefix = $stateRoot + [IO.Path]::DirectorySeparatorChar
$entries = New-Object 'System.Collections.Generic.List[object]'

function Assert-NotReparsePoint {
    param([System.IO.FileSystemInfo]$Item)
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a symbolic link, junction, or other reparse point: $($Item.FullName)"
    }
}

# Check ancestors too: a plain file reached through a junction is not safe.
$ancestor = Get-Item -LiteralPath $sourceRoot -Force
while ($null -ne $ancestor) {
    Assert-NotReparsePoint $ancestor
    $ancestor = $ancestor.Parent
}

function Get-SafeSourceItem {
    param([string]$RelativePath)
    $root = $sourceRoot
    if ($RelativePath -match '^(runtime|data)([\\/]|$)') { $root = $stateRoot }
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source path escapes its allowed root: $RelativePath"
    }
    $item = Get-Item -LiteralPath $path -Force
    Assert-NotReparsePoint $item
    if ($item.PSIsContainer) { $ancestor = $item.Parent }
    else { $ancestor = $item.Directory }
    while ($null -ne $ancestor) {
        Assert-NotReparsePoint $ancestor
        $ancestor = $ancestor.Parent
    }
    return $item
}

function Add-PackageFile {
    param([string]$RelativePath)
    $item = Get-SafeSourceItem $RelativePath
    if ($item.PSIsContainer) { throw "Expected a file: $RelativePath" }
    $entries.Add([pscustomobject]@{ Source = $item.FullName; Name = $RelativePath.Replace('\', '/') })
}

function Add-PackageTree {
    param([string]$RelativePath, [bool]$RuntimeOnly)
    $directory = Get-SafeSourceItem $RelativePath
    if (-not $directory.PSIsContainer) { throw "Expected a directory: $RelativePath" }
    foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
        Assert-NotReparsePoint $item
        # These directories are never part of a migration, even if nested.
        if ($item.Name -in @('.git', 'verification', 'logs', 'cache', 'tools', 'keys', 'staging', '.staging')) { continue }
        $child = Join-Path $RelativePath $item.Name
        if ($item.PSIsContainer) {
            Add-PackageTree $child $RuntimeOnly
        } else {
            if ($RuntimeOnly -and ($item.Name -match '(?i)^(testkey\.|adbkey($|\.)|webview\.apk$)' -or
                $item.Extension -match '(?i)^\.(pk8|key|pfx|p12|jks|keystore|zip|7z|rar|tar|tgz|gz|bz2|xz)$')) {
                continue
            }
            # Emulator resources include required overlay APKs recorded in installation.json.
            # These allowlisted runtime trees must remain intact for import validation.
            Add-PackageFile $child
        }
    }
}

function Assert-AvdUnlocked {
    param([string]$RelativePath)
    $directory = Get-SafeSourceItem $RelativePath
    if (-not $directory.PSIsContainer) { throw "Expected an AVD directory: $RelativePath" }
    foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
        Assert-NotReparsePoint $item
        if ($item.Name -match '(?i)\.lock($|\.)') {
            if ($item.PSIsContainer) {
                throw "AVD lock directory exists: $($item.FullName). Stop the phone first; investigate stale locks manually."
            }
            # Some emulator lock files persist after shutdown. Test exclusivity
            # and byte-range locking without deleting or changing their contents.
            $lockStream = $null
            try {
                $lockStream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $lockStream.Lock(0, 1)
                $lockStream.Unlock(0, 1)
            } catch {
                throw "Cannot establish an unlocked AVD: $($item.FullName). $($_.Exception.Message)"
            } finally {
                if ($null -ne $lockStream) { $lockStream.Dispose() }
            }
        } elseif ($item.PSIsContainer) {
            Assert-AvdUnlocked (Join-Path $RelativePath $item.Name)
        }
    }
}

function Assert-SourceStopped {
    # Fail closed if process inspection is unavailable; never terminate a phone.
    try {
        $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    } catch {
        throw "Cannot check running processes safely. Local packaging is refused. $($_.Exception.Message)"
    }
    foreach ($process in $processes) {
        if ($process.Name -notmatch '(?i)^(DroidNest.*|qemu.*|scrcpy.*|emulator|adb)\.exe$') { continue }
        $commandLine = [string]$process.CommandLine
        $executable = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($commandLine) -and [string]::IsNullOrWhiteSpace($executable)) {
            throw "Cannot inspect $($process.Name) (PID $($process.ProcessId)); stop it or retry with permission to inspect it."
        }
        $reference = ($executable + ' ' + $commandLine).Replace('/', '\')
        if ($reference.IndexOf($sourcePrefix.Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $reference.IndexOf($statePrefix.Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $process.Name -match '(?i)^DroidNest.*\.exe$') {
            throw "Local packaging requires a stopped phone and client: $($process.Name) (PID $($process.ProcessId))."
        }
    }
    if ($IncludeData -and (Test-Path -LiteralPath (Join-Path $stateRoot 'data\avd'))) {
        Assert-AvdUnlocked 'data\avd'
    }
}

$clientFiles = @(
    'DroidNest.exe',
    'DroidNest.exe.config',
    'Start-DroidNest.cmd',
    'Check-Environment.ps1',
    'New-PortablePackage.ps1',
    'tools\SignWebView.java',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'RELEASE-NOTES.md'
    'VALIDATION.md'
)
foreach ($relative in $clientFiles) { Add-PackageFile $relative }
if (Test-Path -LiteralPath (Join-Path $sourceRoot 'CHANGELOG.md')) {
    Add-PackageFile 'CHANGELOG.md'
}

if ($Mode -eq 'Local') {
    Write-Warning 'LOCAL-ONLY: This archive contains locally licensed third-party runtime files. NEVER upload it as a public release.'
    Assert-SourceStopped
    $requiredRuntime = @(
        'runtime\sdk\emulator\emulator.exe',
        'runtime\sdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe',
        'runtime\sdk\platform-tools\adb.exe',
        'runtime\sdk\platform-tools\AdbWinApi.dll',
        'runtime\sdk\platform-tools\AdbWinUsbApi.dll',
        'runtime\scrcpy-win64-v5.0.1\scrcpy.exe',
        'runtime\scrcpy-win64-v5.0.1\scrcpy-server',
        'runtime\webview-local.apk'
    )
    $imageDirectory = 'runtime\sdk\system-images\android-30\default\x86_64'
    foreach ($name in @('system.img', 'userdata.img', 'ramdisk.img', 'kernel-ranchu', 'vendor.img', 'encryptionkey.img', 'source.properties', 'advancedFeatures.ini')) {
        $requiredRuntime += Join-Path $imageDirectory $name
    }
    foreach ($relative in $requiredRuntime) {
        $file = Get-SafeSourceItem $relative
        if ($file.PSIsContainer -or $file.Length -eq 0) { throw "Missing or empty runtime file: $relative" }
    }
    foreach ($relative in @('runtime\sdk\emulator', 'runtime\sdk\platform-tools', $imageDirectory, 'runtime\scrcpy-win64-v5.0.1')) {
        Add-PackageTree $relative $true
    }
    Add-PackageFile 'runtime\webview-local.apk'
    if (Test-Path -LiteralPath (Join-Path $stateRoot 'runtime\installation.json')) {
        Add-PackageFile 'runtime\installation.json'
    }
    foreach ($record in @('runtime\installation-licenses.txt', 'runtime\installation-plan.txt')) {
        if (Test-Path -LiteralPath (Join-Path $stateRoot $record)) { Add-PackageFile $record }
    }
    if ($IncludeData) {
        Write-Warning 'PRIVATE PHONE DATA INCLUDED: Installed apps, credentials, Android identity, and ADB keys may be present. Keep this archive private.'
        Add-PackageTree 'data' $false
    }
}

$suffix = 'client'
if ($Mode -eq 'Local') {
    $suffix = 'LOCAL-ONLY'
    if ($IncludeData) { $suffix += '-with-data' }
}
$baseName = "DroidNest-v$Version-win-x64-$suffix"
$dist = Join-Path $sourceRoot 'dist'
if (Test-Path -LiteralPath $dist) {
    $distItem = Get-SafeSourceItem 'dist'
    if (-not $distItem.PSIsContainer) { throw 'dist exists but is not a directory.' }
} else {
    New-Item -ItemType Directory -Path $dist | Out-Null
}
$zipPath = Join-Path $dist ($baseName + '.zip')
$checksumPath = Join-Path $dist ($baseName + '-SHA256SUMS.txt')
foreach ($path in @($zipPath, $checksumPath)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing output: $path" }
}

Add-Type -AssemblyName System.IO.Compression
$staging = Join-Path $dist ('.packaging-' + [Guid]::NewGuid().ToString('N'))
$ownsStaging = $false
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    $ownsStaging = $true
    $stagedZip = Join-Path $staging ($baseName + '.zip')
    $stagedChecksum = Join-Path $staging ($baseName + '-SHA256SUMS.txt')
    if ($Mode -eq 'Local') { Assert-SourceStopped }
    $zipStream = $null
    $archive = $null
    try {
        $zipStream = [IO.File]::Open($stagedZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $archive = New-Object IO.Compression.ZipArchive -ArgumentList $zipStream, ([IO.Compression.ZipArchiveMode]::Create), $true
        foreach ($entry in $entries) {
            # Recheck immediately before opening; never follow a changed junction.
            $file = Get-SafeSourceItem $entry.Name
            $inputStream = $null
            $outputStream = $null
            try {
                $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $zipEntry = $archive.CreateEntry($entry.Name, [IO.Compression.CompressionLevel]::Optimal)
                $outputStream = $zipEntry.Open()
                $inputStream.CopyTo($outputStream, 1048576)
            } finally {
                if ($null -ne $outputStream) { $outputStream.Dispose() }
                if ($null -ne $inputStream) { $inputStream.Dispose() }
            }
        }
    } finally {
        try {
            if ($null -ne $archive) { $archive.Dispose() }
        } finally {
            if ($null -ne $zipStream) { $zipStream.Dispose() }
        }
    }
    if ($Mode -eq 'Local') { Assert-SourceStopped }
    $hashStream = [IO.File]::OpenRead($stagedZip)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($hashStream))).Replace('-', '').ToLowerInvariant() }
    finally { $hashStream.Dispose(); $sha.Dispose() }
    [IO.File]::WriteAllText($stagedChecksum, "$hash  $baseName.zip`r`n", [Text.Encoding]::ASCII)
    # File.Move does not overwrite, including if another packager won a race.
    # Never delete published outputs on failure; only this invocation's staging.
    [IO.File]::Move($stagedZip, $zipPath)
    [IO.File]::Move($stagedChecksum, $checksumPath)
    Write-Output "Package: $zipPath"
    Write-Output "SHA256:  $checksumPath"
    if ($Mode -eq 'Local') { Write-Warning 'LOCAL-ONLY archive created. Do not attach it to GitHub Releases or share it publicly.' }
} finally {
    if ($ownsStaging -and (Test-Path -LiteralPath $staging)) {
        $cleanupTarget = [IO.Path]::GetFullPath($staging)
        $distPrefix = [IO.Path]::GetFullPath($dist).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
        if (-not $cleanupTarget.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Packaging cleanup target escapes dist.'
        }
        Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
    }
}
