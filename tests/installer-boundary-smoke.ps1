#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Load code without starting the WPF entry point or constructing an installer.
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'DroidNest.exe'))
$type = $assembly.GetType('DroidNest.RuntimeInstaller', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$safe = $type.GetMethod('SafePath', $flags)
$extract = $type.GetMethod('ExtractAsync', $flags)
if ($null -eq $safe -or $null -eq $extract) { throw 'Installer boundary methods are unavailable.' }
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('DroidNest-boundaries-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workspace) | Out-Null

function Assert-InvalidData {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [IO.InvalidDataException]) { $rejected = $true; break }
            $exception = $exception.InnerException
        }
        if (-not $rejected) { throw }
    }
    if (-not $rejected) { throw ('Unsafe input accepted: ' + $Name) }
}

function New-FixtureZip {
    param([string]$Path, [object[]]$Entries)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $zip = $null
    try {
        $zip = New-Object IO.Compression.ZipArchive -ArgumentList $stream, ([IO.Compression.ZipArchiveMode]::Create), $true
        foreach ($item in $Entries) {
            $entry = $zip.CreateEntry($item.Name)
            $entry.ExternalAttributes = $item.Attributes
            if (-not $item.Name.EndsWith('/')) {
                $writer = New-Object IO.StreamWriter -ArgumentList ($entry.Open())
                try { $writer.Write('fixture payload') } finally { $writer.Dispose() }
            }
        }
    } finally {
        if ($null -ne $zip) { $zip.Dispose() }
        $stream.Dispose()
    }
}

function Invoke-Extraction {
    param([string]$Archive, [string]$Destination, [AllowNull()][string]$Prefix)
    $task = $extract.Invoke($null, @([string]$Archive, [string]$Destination, [string]$Prefix, [Threading.CancellationToken]::None))
    $task.GetAwaiter().GetResult()
}

try {
    $output = Join-Path $workspace 'output'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $outside = Join-Path $workspace 'outside.txt'
    [IO.File]::WriteAllText($outside, 'outside sentinel')
    $unsafePaths = @('../outside.txt', '..\outside.txt', 'a/../../outside.txt', 'a/../outside.txt',
        '/outside.txt', '\outside.txt', '\\server\share\outside.txt', 'C:\outside.txt', 'C:outside.txt',
        'bad:stream', 'CON.txt', 'NUL', 'aux.log', 'COM1.txt', 'LPT9', 'name.', 'name ', './file', 'a//file', '')
    foreach ($relative in $unsafePaths) {
        Assert-InvalidData { $null = $safe.Invoke($null, @([string]$output, [string]$relative)) } ('path ' + $relative)
    }
    foreach ($relative in @('bin/java.exe', 'sdk/system-images/android-30/default/x86_64/system.img', 'space name/file.txt')) {
        $actual = $safe.Invoke($null, @([string]$output, [string]$relative))
        $expected = [IO.Path]::GetFullPath((Join-Path $output $relative))
        if ($actual -cne $expected) { throw ('Valid path did not resolve inside the supplied root: ' + $relative) }
    }

    $hostile = @(
        @{ Name = 'root/../outside.txt'; Attributes = 0 },
        @{ Name = 'root/..\outside.txt'; Attributes = 0 },
        @{ Name = 'root/a/../../outside.txt'; Attributes = 0 },
        @{ Name = 'root//outside.txt'; Attributes = 0 },
        @{ Name = 'root/' + $outside; Attributes = 0 },
        @{ Name = 'root/bad:stream'; Attributes = 0 },
        @{ Name = 'root/CON.txt'; Attributes = 0 },
        @{ Name = 'root/name.'; Attributes = 0 },
        @{ Name = 'root/name '; Attributes = 0 },
        @{ Name = 'other/file.txt'; Attributes = 0 },
        @{ Name = 'root/link'; Attributes = -1610612736 }, # Unix symbolic-link type (0xa000 << 16).
        @{ Name = 'root/reparse'; Attributes = 1024 }
    )
    for ($index = 0; $index -lt $hostile.Count; $index++) {
        $archive = Join-Path $workspace ('hostile-' + $index + '.zip')
        New-FixtureZip $archive @($hostile[$index])
        Assert-InvalidData { Invoke-Extraction $archive $output 'root/' } $hostile[$index].Name
        if (@(Get-ChildItem -LiteralPath $output -Recurse -Force -File).Count -ne 0) {
            throw 'Rejected single-entry ZIP wrote a file into staging.'
        }
        if ([IO.File]::ReadAllText($outside) -cne 'outside sentinel') { throw 'ZIP changed a file outside staging.' }
    }
    $corrupt = Join-Path $workspace 'corrupt.zip'
    [IO.File]::WriteAllText($corrupt, 'not a ZIP archive')
    Assert-InvalidData { Invoke-Extraction $corrupt $output 'root/' } 'corrupt ZIP'

    # Positive control: rejection cannot pass merely because all extraction is broken.
    $valid = Join-Path $workspace 'valid.zip'
    New-FixtureZip $valid @(@{ Name = 'root/bin/'; Attributes = 0 }, @{ Name = 'root/bin/tool.txt'; Attributes = 0 })
    Invoke-Extraction $valid $output 'root/'
    if ([IO.File]::ReadAllText((Join-Path $output 'bin/tool.txt')) -cne 'fixture payload') {
        throw 'Valid ZIP payload did not extract correctly.'
    }
    if (@(Get-ChildItem -LiteralPath $output -Recurse -Force -File).Count -ne 1) {
        throw 'Unexpected files after valid extraction.'
    }
    if ([IO.File]::ReadAllText($outside) -cne 'outside sentinel') { throw 'Extraction changed the outside sentinel.' }
    Write-Output 'PASS: installer path and ZIP boundaries reject traversal, aliases, links and malformed archives; valid ZIP extracts.'
} finally {
    Remove-Item -LiteralPath $workspace -Recurse -Force
}
