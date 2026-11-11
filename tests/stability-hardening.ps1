#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'DroidNest.exe'))
$installer = $assembly.GetType('DroidNest.RuntimeInstaller', $true)
$runtime = $assembly.GetType('DroidNest.AndroidRuntime', $true)
$avd = $assembly.GetType('DroidNest.AvdConfig', $true)
$downloadType = $assembly.GetType('DroidNest.RuntimeDownload', $true)
$static = [Reflection.BindingFlags]'Static,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('DroidNest-stability-' + [Guid]::NewGuid().ToString('N'))
function Assert-Rejected([scriptblock]$Action, [type]$ExceptionType, [string]$Scenario) {
    try { & $Action } catch {
        if ($_.Exception.GetBaseException() -is $ExceptionType) { return }
        throw ($Scenario + ': ' + $_.Exception.GetBaseException().GetType().Name)
    }
    throw ('Expected rejection: ' + $Scenario)
}
function Invoke-Task($Task) {
    $Task.GetAwaiter().GetResult() | Out-Null
}
[IO.Directory]::CreateDirectory($workspace) | Out-Null
try {
    $payload = New-Object byte[] 262144
    $random = New-Object Security.Cryptography.RNGCryptoServiceProvider
    try { $random.GetBytes($payload) } finally { $random.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash($payload)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $downloadCtor = $downloadType.GetConstructors($instance)[0]
    $download = $downloadCtor.Invoke(@('fixture', 'fixture', 'https://example.invalid/archive.zip', $digest, 'SHA256', [long]$payload.Length, 'root/', 'runtime/fixture'))

    # Content-Range negotiation for resumed transfers.
    $parse = $installer.GetMethod('ParseContentRange', $static)
    if ([long]$parse.Invoke($null, @('bytes 1024-262143/262144', [long]1024, [long]262144)) -ne 1024) { throw 'Valid resumed Content-Range rejected.' }
    foreach ($case in @(
            @('bytes 1023-262143/262144', [long]1024),
            @('bytes 1024-262142/262144', [long]1024),
            @('bytes 1024-262143/262145', [long]1024),
            @('items 1024-262143/262144', [long]1024),
            @('bytes 1024-262143/*', [long]1024),
            @('bytes 262144-262143/262144', [long]1024),
            @('', [long]1024))) {
        $header = $case[0]
        $offset = $case[1]
        Assert-Rejected { $parse.Invoke($null, @([string]$header, [long]$offset, [long]262144)) } ([IO.InvalidDataException]) ('Content-Range ' + $header)
    }

    # Only a plausible local partial is offered for resume.
    $resume = $installer.GetMethod('ResumeOffset', $static)
    $partial = Join-Path $workspace 'archive.partial'
    if ([long]$resume.Invoke($null, @([string]$partial, [long]262144)) -ne 0) { throw 'Missing partial offered for resume.' }
    [IO.File]::WriteAllBytes($partial, (New-Object byte[] 4096))
    if ([long]$resume.Invoke($null, @([string]$partial, [long]262144)) -ne 4096) { throw 'Valid partial offset not reused.' }
    $stream = [IO.File]::Open($partial, [IO.FileMode]::Append)
    try { $stream.SetLength(262145) } finally { $stream.Dispose() }
    if ([long]$resume.Invoke($null, @([string]$partial, [long]262144)) -ne 0) { throw 'Oversized partial offered for resume.' }
    if (Test-Path -LiteralPath $partial) { throw 'Oversized partial was not discarded.' }

    # A resumed transfer must verify prefix plus suffix, and reject a spliced corrupt prefix.
    $copy = $installer.GetMethod('CopyPartAsync', $static)
    $target = Join-Path $workspace 'copy.partial'
    [byte[]]$prefix = $payload[0..1023]
    [IO.File]::WriteAllBytes($target, $prefix)
    [byte[]]$suffix = $payload[1024..($payload.Length - 1)]
    $cts = [Threading.CancellationTokenSource]::new()
    try {
        $source = [IO.MemoryStream]::new($suffix)
        try { Invoke-Task ($copy.Invoke($null, @($source, [string]$target, [long]1024, $download, $null, $cts))) } finally { $source.Dispose() }
    } finally { $cts.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($target))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($actual -cne $digest) { throw 'Resumed transfer did not reproduce the pinned digest.' }
    [IO.File]::WriteAllBytes($target, (New-Object byte[] 1024))
    $cts = [Threading.CancellationTokenSource]::new()
    try {
        $source = [IO.MemoryStream]::new($suffix)
        try { Assert-Rejected { Invoke-Task ($copy.Invoke($null, @($source, [string]$target, [long]1024, $download, $null, $cts))) } ([IO.InvalidDataException]) 'Spliced corrupt prefix' }
        finally { $source.Dispose() }
    } finally { $cts.Dispose() }
    Assert-Rejected { Invoke-Task ($copy.Invoke($null, @(([IO.MemoryStream]::new($suffix)), [string]$target, [long]0, $download, $null, ([Threading.CancellationTokenSource]::new())))) } ([IO.InvalidDataException]) 'Truncated full transfer'

    # AVD tuning keys survive regeneration; structural keys and out-of-range values do not.
    $merge = $avd.GetMethod('Merge', $static)
    $custom = "hw.ramSize=4096`r`nhw.cpu.ncore=4`r`nhw.lcd.width=1080`r`nhw.lcd.height=1920`r`nhw.lcd.density=420`r`n" +
        "vm.heapSize=512`r`ndisk.dataPartition.size=8G`r`nhw.gpu.mode=host`r`nunknown.key=1`r`nAvdId=Other`r`n" +
        "image.sysdir.1=C:/evil`r`nskin.name=1x1`r`n"
    $merged = [string]$merge.Invoke($null, @([string]$custom, 'C:\runtime\image'))
    foreach ($expected in @('hw.ramSize=4096', 'hw.cpu.ncore=4', 'hw.lcd.width=1080', 'hw.lcd.height=1920',
            'hw.lcd.density=420', 'vm.heapSize=512', 'disk.dataPartition.size=8G', 'hw.gpu.mode=host',
            'skin.name=1080x1920', 'image.sysdir.1=C:/runtime/image/', 'AvdId=DroidNest')) {
        if (-not $merged.Contains($expected)) { throw ('AVD merge dropped ' + $expected) }
    }
    foreach ($rejected in @('unknown.key', 'AvdId=Other', 'C:/evil', 'skin.name=1x1')) {
        if ($merged.Contains($rejected)) { throw ('AVD merge kept ' + $rejected) }
    }
    $gpu = $avd.GetMethod('GpuMode', $static)
    if ([string]$gpu.Invoke($null, @([string]$merged)) -cne 'host') { throw 'Launch GPU mode ignored the preserved config.' }
    $invalid = [string]$merge.Invoke($null, @([string]"hw.ramSize=1`nhw.cpu.ncore=0`nhw.lcd.width=99999`nhw.lcd.height=-1`nhw.lcd.density=9999`nvm.heapSize=0`ndisk.dataPartition.size=1K`nhw.gpu.mode=guest`n", 'C:\image'))
    foreach ($expected in @('hw.ramSize=2048', 'hw.cpu.ncore=2', 'hw.lcd.width=720', 'hw.lcd.height=1280',
            'hw.lcd.density=320', 'vm.heapSize=256', 'disk.dataPartition.size=4G', 'hw.gpu.mode=swiftshader')) {
        if (-not $invalid.Contains($expected)) { throw ('AVD merge accepted invalid ' + $expected) }
    }
    # The emulator rewrites the file with spaces and expands 8G into bytes; both must survive.
    $rewritten = [string]$merge.Invoke($null, @([string]"hw.ramSize = 3072`nhw.cpu.ncore = 4`nhw.lcd.width = 540`nhw.lcd.height = 960`n disk.dataPartition.size = 6442450944`n", 'C:\image'))
    foreach ($expected in @('hw.ramSize=3072', 'hw.cpu.ncore=4', 'hw.lcd.width=540', 'skin.name=540x960',
            'disk.dataPartition.size=6442450944')) {
        if (-not $rewritten.Contains($expected)) { throw ('AVD merge lost emulator-rewritten ' + $expected) }
    }

    # The real PrepareAvd path writes defaults, then carries an edited config.ini into the next start.
    $runtimeCtor = $runtime.GetConstructors([Reflection.BindingFlags]'Instance,Public')[0]
    $root = Join-Path $workspace 'app'
    $instanceRuntime = $runtimeCtor.Invoke(@([string]$root))
    $prepare = $runtime.GetMethod('PrepareAvd', $instance)
    $configPath = Join-Path $root 'data/avd/DroidNest.avd/config.ini'
    $prepare.Invoke($instanceRuntime, @()) | Out-Null
    $first = [IO.File]::ReadAllText($configPath)
    if (-not $first.Contains('hw.ramSize=2048') -or -not $first.Contains('skin.name=720x1280')) { throw 'PrepareAvd did not write defaults.' }
    [IO.File]::WriteAllText($configPath, "hw.ramSize=3072`nhw.lcd.width=800`nhw.lcd.height=1280`nhw.lcd.density=240`nhw.gpu.mode=angle`n")
    $prepare.Invoke($instanceRuntime, @()) | Out-Null
    $second = [IO.File]::ReadAllText($configPath)
    foreach ($expected in @('hw.ramSize=3072', 'hw.lcd.width=800', 'hw.lcd.density=240', 'hw.gpu.mode=angle', 'skin.name=800x1280')) {
        if (-not $second.Contains($expected)) { throw ('PrepareAvd did not preserve ' + $expected) }
    }

    # Log rotation keeps the newest sessions, caps total size, and never deletes a fresh log.
    $trim = $runtime.GetMethod('TrimLogs', $static)
    $logs = Join-Path $workspace 'logs'
    [IO.Directory]::CreateDirectory($logs) | Out-Null
    $now = [DateTime]::UtcNow
    for ($index = 0; $index -lt 20; $index++) {
        $path = Join-Path $logs ('session-' + $index.ToString('00') + '.log')
        [IO.File]::WriteAllText($path, 'line')
        [IO.File]::SetLastWriteTimeUtc($path, $now.AddHours(-5).AddMinutes($index))
    }
    [IO.File]::WriteAllText((Join-Path $logs 'notes.txt'), 'not a log')
    $deleted = [int]$trim.Invoke($null, @([string]$logs, 16, [long]67108864, $now))
    if ($deleted -ne 4) { throw ('Log rotation deleted ' + $deleted + ' files instead of 4.') }
    if ((Get-ChildItem -LiteralPath $logs -Filter '*.log').Count -ne 16) { throw 'Log rotation kept the wrong file count.' }
    if (-not (Test-Path -LiteralPath (Join-Path $logs 'notes.txt'))) { throw 'Log rotation touched a non-log file.' }
    for ($index = 0; $index -lt 4; $index++) {
        $big = Join-Path $logs ('big-' + $index.ToString('00') + '.log')
        [IO.File]::WriteAllBytes($big, (New-Object byte[] 1048576))
        [IO.File]::SetLastWriteTimeUtc($big, $now.AddHours(-3).AddMinutes($index))
    }
    $deleted = [int]$trim.Invoke($null, @([string]$logs, 16, [long]2097152, $now))
    if ($deleted -lt 2) { throw 'Log rotation ignored the size cap.' }
    $total = (Get-ChildItem -LiteralPath $logs -Filter '*.log' | Measure-Object Length -Sum).Sum
    if ($total -gt 3145728) { throw ('Log rotation left ' + $total + ' bytes.') }
    $fresh = Join-Path $logs 'active.log'
    [IO.File]::WriteAllText($fresh, 'active')
    [IO.File]::SetLastWriteTimeUtc($fresh, $now)
    $null = $trim.Invoke($null, @([string]$logs, 0, [long]0, $now))
    if (-not (Test-Path -LiteralPath $fresh)) { throw 'Log rotation deleted a log that is still being written.' }

    Write-Output 'PASS: Content-Range negotiation, partial resume limits, resumed digest verification, AVD tuning preservation and log rotation.'
} finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
