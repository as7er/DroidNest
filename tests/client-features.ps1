#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'DroidNest.exe'))
$settingsType = $assembly.GetType('DroidNest.ClientSettings', $true)
$backupType = $assembly.GetType('DroidNest.PhoneBackup', $true)
$installer = $assembly.GetType('DroidNest.RuntimeInstaller', $true)
$static = [Reflection.BindingFlags]'Static,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$none = [Threading.CancellationToken]::None
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('DroidNest-features-' + [Guid]::NewGuid().ToString('N'))
function Assert-Rejected([scriptblock]$Action, [type]$ExceptionType, [string]$Scenario) {
    try { & $Action } catch {
        if ($_.Exception.GetBaseException() -is $ExceptionType) { return }
        throw ($Scenario + ': ' + $_.Exception.GetBaseException().GetType().Name)
    }
    throw ('Expected rejection: ' + $Scenario)
}
function Field($Object, [string]$Name) {
    return $Object.GetType().GetField($Name, $instance).GetValue($Object)
}
function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Write-Zip([string]$Path, [hashtable]$Entries) {
    $file = [IO.File]::Create($Path)
    try {
        $zip = New-Object IO.Compression.ZipArchive -ArgumentList $file, ([IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $Entries.Keys) {
                $entry = $zip.CreateEntry([string]$name)
                $stream = $entry.Open()
                try {
                    $bytes = [byte[]]$Entries[$name]
                    $stream.Write($bytes, 0, $bytes.Length)
                } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $file.Dispose() }
}
[IO.Directory]::CreateDirectory($workspace) | Out-Null
try {
    # Preferences default safely, round-trip, and ignore a damaged file.
    $load = $settingsType.GetMethod('Load', $static)
    $save = $settingsType.GetMethod('Save', $instance)
    $field = $settingsType.GetField('AutoStartPhone', $instance)
    $prefsRoot = Join-Path $workspace 'prefs'
    [IO.Directory]::CreateDirectory($prefsRoot) | Out-Null
    $settings = $load.Invoke($null, @([string]$prefsRoot))
    if (-not [bool]$field.GetValue($settings)) { throw 'Default auto-start preference is not enabled.' }
    $field.SetValue($settings, $false)
    $save.Invoke($settings, @()) | Out-Null
    $reloaded = $load.Invoke($null, @([string]$prefsRoot))
    if ([bool]$field.GetValue($reloaded)) { throw 'Auto-start preference did not survive a restart.' }
    $document = [IO.File]::ReadAllText((Join-Path $prefsRoot 'settings.json')) | ConvertFrom-Json
    if ($document.autoStartPhone -ne $false) { throw 'Saved preference has the wrong shape.' }
    [IO.File]::WriteAllText((Join-Path $prefsRoot 'settings.json'), '{ broken')
    $recovered = $load.Invoke($null, @([string]$prefsRoot))
    if (-not [bool]$field.GetValue($recovered)) { throw 'Damaged preferences did not fall back to defaults.' }

    # Phone data backup and restore preserve bytes and never overwrite existing data.
    $create = $backupType.GetMethod('Create', $static)
    $restore = $backupType.GetMethod('Restore', $static)
    $source = Join-Path $workspace 'source'
    $data = Join-Path $source 'data'
    [IO.Directory]::CreateDirectory((Join-Path $data 'avd')) | Out-Null
    $payload = New-Object byte[] 4096
    (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($payload)
    [IO.File]::WriteAllText((Join-Path $data 'note.txt'), 'phone state')
    [IO.File]::WriteAllBytes((Join-Path $data 'avd/disk.bin'), $payload)
    $archive = Join-Path $workspace 'backup.zip'
    $created = [string]$create.Invoke($null, @([string]$source, [string]$archive, $none))
    if ($created -cne $archive) { throw 'Backup reported an unexpected path.' }
    $sidecar = [IO.File]::ReadAllText($archive + '.sha256')
    if ($sidecar.Trim() -cne ((Get-Sha256 $archive) + '  backup.zip')) { throw 'Backup checksum sidecar is wrong.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        if ($names -notcontains 'data/note.txt' -or $names -notcontains 'data/avd/disk.bin') { throw 'Backup omitted phone data.' }
    } finally { $zip.Dispose() }

    $target = Join-Path $workspace 'target'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $restore.Invoke($null, @([string]$target, [string]$archive, $none)) | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $target 'data/note.txt')) -cne 'phone state') { throw 'Restored text differs.' }
    if ((Get-Sha256 (Join-Path $target 'data/avd/disk.bin')) -cne (Get-Sha256 (Join-Path $data 'avd/disk.bin'))) { throw 'Restored binary differs.' }

    $occupied = Join-Path $workspace 'occupied'
    [IO.Directory]::CreateDirectory((Join-Path $occupied 'data')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $occupied 'data/existing.txt'), 'keep')
    Assert-Rejected { $restore.Invoke($null, @([string]$occupied, [string]$archive, $none)) } ([InvalidOperationException]) 'Restore over existing data'
    if ([IO.File]::ReadAllText((Join-Path $occupied 'data/existing.txt')) -cne 'keep') { throw 'Restore changed existing data.' }

    $hostile = Join-Path $workspace 'hostile.zip'
    Write-Zip $hostile ([ordered]@{ 'evil.txt' = [byte[]](65) })
    Assert-Rejected { $restore.Invoke($null, @([string](Join-Path $workspace 'fresh1'), [string]$hostile, $none)) } ([IO.InvalidDataException]) 'Restore with foreign top-level entry'
    $traversal = Join-Path $workspace 'traversal.zip'
    Write-Zip $traversal ([ordered]@{ 'data/../escape.txt' = [byte[]](65) })
    Assert-Rejected { $restore.Invoke($null, @([string](Join-Path $workspace 'fresh2'), [string]$traversal, $none)) } ([IO.InvalidDataException]) 'Restore with traversal entry'
    $tampered = Join-Path $workspace 'tampered.zip'
    Copy-Item -LiteralPath $archive -Destination $tampered
    [IO.File]::WriteAllText($tampered + '.sha256', ('0' * 64) + '  tampered.zip' + [Environment]::NewLine)
    Assert-Rejected { $restore.Invoke($null, @([string](Join-Path $workspace 'fresh3'), [string]$tampered, $none)) } ([IO.InvalidDataException]) 'Restore with mismatched checksum'

    # Runtime health distinguishes old receipts, damaged files and missing build tools.
    $health = $installer.GetMethod('DescribeHealth', $static)
    $old = Join-Path $workspace 'old'
    [IO.Directory]::CreateDirectory((Join-Path $old 'runtime')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $old 'runtime/installation.json'), '{"format":1}')
    if (([string]$health.Invoke($null, @([string]$old))).IndexOf('格式为 1') -lt 0) { throw 'Old receipt not reported.' }
    $fresh = Join-Path $workspace 'fresh'
    $freshRuntime = Join-Path $fresh 'runtime'
    [IO.Directory]::CreateDirectory((Join-Path $freshRuntime 'sdk/emulator')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $freshRuntime 'sdk/platform-tools')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $freshRuntime 'installation.json'), '{"format":2}')
    [IO.File]::WriteAllText((Join-Path $freshRuntime 'sdk/emulator/source.properties'), "Pkg.Revision=37.2.13`n")
    [IO.File]::WriteAllText((Join-Path $freshRuntime 'sdk/platform-tools/source.properties'), "Pkg.Revision=37.0.1`n")
    if (([string]$health.Invoke($null, @([string]$fresh))).IndexOf('Build Tools') -lt 0) { throw 'Missing build tools not reported.' }
    [IO.Directory]::CreateDirectory((Join-Path $freshRuntime 'tools/build-tools')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $freshRuntime 'tools/build-tools/aapt2.exe'), 'stub')
    if ($null -ne $health.Invoke($null, @([string]$fresh))) { throw 'Healthy runtime reported as needing repair.' }
    [IO.File]::WriteAllText((Join-Path $freshRuntime 'sdk/emulator/source.properties'), "Pkg.Revision=37.3.9`n")
    if (([string]$health.Invoke($null, @([string]$fresh))).IndexOf('37.3.9') -lt 0) { throw 'Version drift not reported.' }

    # APK inspection without Build Tools reads the binary manifest directly.
    $inspector = $assembly.GetType('DroidNest.ApkInspector', $true)
    $stringValues = @('manifest', 'package', 'versionCode', 'versionName', 'com.example.test', '1.2.3')
    $pool = New-Object IO.MemoryStream
    $poolWriter = New-Object IO.BinaryWriter -ArgumentList $pool
    $stringBytes = New-Object IO.MemoryStream
    $stringWriter = New-Object IO.BinaryWriter -ArgumentList $stringBytes
    $stringOffsets = @()
    foreach ($value in $stringValues) {
        $stringOffsets += [int]$stringBytes.Position
        $encoded = [Text.Encoding]::Unicode.GetBytes($value)
        $stringWriter.Write([uint16]$value.Length)
        $stringWriter.Write($encoded)
        $stringWriter.Write([uint16]0)
    }
    $stringWriter.Flush()
    $poolBytes = $stringBytes.ToArray()
    $poolHeaderSize = 28
    $poolSize = $poolHeaderSize + 4 * $stringValues.Count + $poolBytes.Length
    $poolWriter.Write([uint16]0x0001)
    $poolWriter.Write([uint16]$poolHeaderSize)
    $poolWriter.Write([int]$poolSize)
    $poolWriter.Write([int]$stringValues.Count)
    $poolWriter.Write([int]0)
    $poolWriter.Write([int]0)
    $poolWriter.Write([int]($poolHeaderSize + 4 * $stringValues.Count))
    $poolWriter.Write([int]0)
    foreach ($offset in $stringOffsets) { $poolWriter.Write([int]$offset) }
    $poolWriter.Write($poolBytes)
    $poolWriter.Flush()

    $element = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter -ArgumentList $element
    $attributeCount = 3
    $elementSize = 16 + 20 + 20 * $attributeCount
    $writer.Write([uint16]0x0102)
    $writer.Write([uint16]16)
    $writer.Write([int]$elementSize)
    $writer.Write([int]1)
    $writer.Write([uint32]::MaxValue)
    $writer.Write([uint32]::MaxValue)
    $writer.Write([uint32]0)
    $writer.Write([uint16]20)
    $writer.Write([uint16]20)
    $writer.Write([uint16]$attributeCount)
    $writer.Write([uint16]0)
    $writer.Write([uint16]0)
    $writer.Write([uint16]0)
    foreach ($attribute in @(
            @('package', 3, 4),
            @('versionName', 3, 5),
            @('versionCode', 0x10, 42))) {
        $name = [Array]::IndexOf($stringValues, $attribute[0])
        $writer.Write([uint32]::MaxValue)
        $writer.Write([uint32]$name)
        $writer.Write([uint32]::MaxValue)
        $writer.Write([uint16]8)
        $writer.Write([byte]0)
        $writer.Write([byte]$attribute[1])
        $writer.Write([uint32]$attribute[2])
    }
    $writer.Flush()

    $manifestBytes = New-Object IO.MemoryStream
    $manifestWriter = New-Object IO.BinaryWriter -ArgumentList $manifestBytes
    $manifestWriter.Write([uint16]0x0003)
    $manifestWriter.Write([uint16]8)
    $manifestWriter.Write([int](8 + $poolSize + $elementSize))
    $manifestWriter.Write($pool.ToArray())
    $manifestWriter.Write($element.ToArray())
    $manifestWriter.Flush()

    $synthetic = Join-Path $workspace 'synthetic.apk'
    Write-Zip $synthetic ([ordered]@{ 'AndroidManifest.xml' = $manifestBytes.ToArray() })
    $emptyRoot = Join-Path $workspace 'empty-root'
    [IO.Directory]::CreateDirectory($emptyRoot) | Out-Null
    $inspect = $inspector.GetMethod('Inspect', $static)
    $parsed = $inspect.Invoke($null, @([string]$emptyRoot, [string]$synthetic))
    if ([string](Field $parsed 'Package') -cne 'com.example.test') { throw 'Binary manifest package was not read.' }
    $parsedVersion = [string](Field $parsed 'Version')
    if ($parsedVersion.IndexOf('1.2.3') -lt 0 -or $parsedVersion.IndexOf('42') -lt 0) { throw 'Binary manifest version was not read.' }
    $missing = Join-Path $workspace 'missing-manifest.apk'
    Write-Zip $missing ([ordered]@{ 'classes.dex' = [byte[]](1, 2, 3) })
    $unreadable = $inspect.Invoke($null, @([string]$emptyRoot, [string]$missing))
    if (-not [string]::IsNullOrEmpty([string](Field $unreadable 'Package'))) { throw 'APK without manifest reported a package.' }

    # The public preflight must resolve the disk policy when its probe path comes from Join-Path.
    # Windows PowerShell 5.1 reflection calls do not unwrap the resulting PSObject automatically.
    # Run it only without an installed runtime so the probe cannot touch real phone data; CI covers this path.
    $preflightRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'DroidNest'
    if (Test-Path -LiteralPath (Join-Path $preflightRoot 'runtime/installation.json') -PathType Leaf) {
        Write-Output 'SKIP: preflight disk diagnostic (installed runtime present; CI covers the clean-root path).'
    } else {
        $preflightRootExisted = Test-Path -LiteralPath $preflightRoot -PathType Container
        try {
            $diagnostic = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'Check-Environment.ps1') -Json 2>&1
            $diagnosticExit = $LASTEXITCODE
            # GitHub Actions' PowerShell wrapper exits with the last native code; keep the expected preflight failure local.
            $global:LASTEXITCODE = 0
            if ($diagnosticExit -ne 0 -and $diagnosticExit -ne 1) { throw ('Preflight diagnostic exited with ' + $diagnosticExit) }
            $report = (($diagnostic | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | ConvertFrom-Json
            $diskChecks = @($report.checks | Where-Object { $_.name -like 'disk:*' })
            if ($diskChecks.Count -eq 0) { throw 'Preflight diagnostic did not report the disk policy.' }
            foreach ($check in $diskChecks) {
                if ([string]$check.message -match 'PSObject|无法确认启动前可用磁盘空间') { throw ('Preflight disk diagnostic failed: ' + $check.message) }
            }
        } finally {
            # The diagnostic creates the state root to test writability; undo that only when the test created it.
            if (-not $preflightRootExisted -and (Test-Path -LiteralPath $preflightRoot -PathType Container)) {
                if (@(Get-ChildItem -LiteralPath $preflightRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $preflightRoot -Force }
            }
        }
    }

    Write-Output 'PASS: preferences, phone backup/restore boundaries, runtime health, binary manifest inspection and preflight disk diagnostic.'
} finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
