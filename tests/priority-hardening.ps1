#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem,System.Xml.Linq
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'DroidNest.exe'))
$type = $assembly.GetType('DroidNest.RuntimeInstaller', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$none = [Threading.CancellationToken]::None
$installed = $type.GetMethod('IsInstalled', $flags)
$receipt = $type.GetMethod('WriteReceipt', $flags)
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('DroidNest-priority-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $workspace 'runtime'
function Assert-Installed([bool]$Expected, [string]$Scenario) {
    if ($installed.Invoke($null, @([string]$workspace)) -ne $Expected) { throw $Scenario }
}
function Assert-Rejected([scriptblock]$Action, [type]$ExceptionType) {
    try { & $Action } catch {
        if ($_.Exception.GetBaseException() -is $ExceptionType) { return }
        throw
    }
    throw ('Expected rejection: ' + $ExceptionType.Name)
}
try {
    foreach ($relative in $type.GetField('RequiredFiles', $flags).GetValue($null)) {
        $path = Join-Path $runtime $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, 'original')
    }
    [IO.File]::WriteAllText((Join-Path $runtime 'sdk/system-images/android-30/default/x86_64/source.properties'), "AndroidVersion.ApiLevel=30`nSystemImage.Abi=x86_64`n")
    $receipt.Invoke($null, @([string]$runtime, $none))
    Assert-Installed $true 'Fresh receipt rejected.'
    $recordPath = Join-Path $runtime 'installation.json'
    $record = [IO.File]::ReadAllText($recordPath)
    $adb = Join-Path $runtime 'sdk/platform-tools/adb.exe'
    [IO.File]::WriteAllText($adb, 'tampered')
    Assert-Installed $false 'Same-size executable corruption accepted.'
    [IO.File]::WriteAllText($adb, 'original')
    Assert-Installed $true 'Restored executable rejected.'
    $document = $record | ConvertFrom-Json
    $document.sha256.PSObject.Properties.Remove('sdk/platform-tools/adb.exe')
    [IO.File]::WriteAllText($recordPath, ($document | ConvertTo-Json -Depth 8))
    Assert-Installed $false 'Missing critical hash accepted.'
    $document = $record | ConvertFrom-Json
    $document.sha256 = $null
    [IO.File]::WriteAllText($recordPath, ($document | ConvertTo-Json -Depth 8))
    Assert-Installed $false 'Null hash dictionary accepted.'
    $document = $record | ConvertFrom-Json
    $document.format = 1
    [IO.File]::WriteAllText($recordPath, ($document | ConvertTo-Json -Depth 8))
    Assert-Installed $false 'Length-only receipt silently trusted.'
    [IO.File]::WriteAllText($recordPath, $record)
    [IO.File]::Delete($adb)
    Assert-Installed $false 'Missing executable accepted.'

    $downloadType = $assembly.GetType('DroidNest.RuntimeDownload', $true)
    $ctor = $downloadType.GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
    $download = $ctor.Invoke(@('fixture', 'fixture', 'https://example.invalid/archive.zip', ('a' * 64), 'SHA256', [long]1, 'root/', 'runtime/fixture'))
    $downloads = [Array]::CreateInstance($downloadType, 1)
    $downloads.SetValue($download, 0)
    $archives = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $zipPath = Join-Path $workspace 'fixture.zip'
    $file = [IO.File]::Create($zipPath)
    $zip = New-Object IO.Compression.ZipArchive -ArgumentList $file, ([IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('root/payload')
        $stream = $entry.Open()
        try { $stream.Write((New-Object byte[] 4096), 0, 4096) } finally { $stream.Dispose() }
    } finally { $zip.Dispose(); $file.Dispose() }
    $archives.Add('fixture', $zipPath)
    $disk = $assembly.GetType('DroidNest.RuntimeDiskSpace', $true)
    $reserve = [long]$disk.GetField('PhoneReserveBytes', $flags).GetRawConstantValue()
    $webview = [long]$type.GetField('WebViewSize', $flags).GetRawConstantValue()
    $budget = $type.GetMethod('PreparationBytes', $flags).Invoke($null, @($downloads, $archives.PSObject.BaseObject, $none))
    if ($budget -ne ($reserve + $webview + 40MB + 4096 + 65536)) { throw 'Budget omitted expanded payload, allocation allowance, signing or phone reserve.' }
    $require = $disk.GetMethod('Require', $flags)
    Assert-Rejected { $require.Invoke($null, @([string]$workspace, [long]::MaxValue)) } ([IO.IOException])
    Assert-Rejected { $require.Invoke($null, @([string]$workspace, [long]-1)) } ([ArgumentOutOfRangeException])

    $xml = '<repository><license id="license">test</license><remotePackage path="platform-tools"><revision><major>37</major><minor>0</minor><micro>1</micro></revision><channelRef ref="channel-0"/><uses-license ref="license"/><archives><archive><host-os>windows</host-os><complete><size>8044989</size><checksum type="sha1">0000000000000000000000000000000000000000</checksum><url>platform-tools_r37.0.1-win.zip</url></complete></archive></archives></remotePackage></repository>'
    $licenses = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $android = $type.GetMethod('AndroidPackage', $flags)
    $arguments = @([Xml.Linq.XDocument]::Parse($xml), 'platform-tools', '37.0.1', 'platform-tools', 'platform-tools/', 'runtime/sdk/platform-tools', 'https://dl.google.com/android/repository/', $licenses.PSObject.BaseObject)
    $result = $android.Invoke($null, $arguments)
    $fieldFlags = [Reflection.BindingFlags]'Instance,NonPublic'
    if ($downloadType.GetField('Digest', $fieldFlags).GetValue($result) -cne '45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99') { throw 'Untrusted metadata replaced compiled archive digest.' }
    $arguments[0] = [Xml.Linq.XDocument]::Parse($xml.Replace('platform-tools_r37.0.1-win.zip', 'substitute.zip'))
    Assert-Rejected { $null = $android.Invoke($null, $arguments) } ([IO.InvalidDataException])
    Write-Output 'PASS: same-size corruption, missing hashes/files, old receipts, expanded disk budget, insufficient space and pinned archive substitution.'
} finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
