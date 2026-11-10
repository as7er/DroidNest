param([string]$JavaHome, [string]$ApkSignerJar)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
function Hash([string]$Path) {
 $stream=[IO.File]::OpenRead((Join-Path $PSScriptRoot $Path)); $sha=[Security.Cryptography.SHA256]::Create()
 try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
 finally { $stream.Dispose(); $sha.Dispose() }
}
function Fetch([string]$Url,[string]$Path,[string]$Digest) {
 if ((Test-Path $Path) -and (Hash $Path) -eq $Digest) { return }
 & curl.exe --ssl-revoke-best-effort -fL --retry 2 $Url -o $Path
 if ($LASTEXITCODE -ne 0 -or (Hash $Path) -ne $Digest) { throw "Download/hash verification failed: $Path" }
}
New-Item -ItemType Directory runtime -Force | Out-Null
Fetch 'https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip' 'runtime/scrcpy-win64-v5.0.1.zip' 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5'
if (!(Test-Path runtime/scrcpy-win64-v5.0.1/scrcpy.exe)) { Expand-Archive runtime/scrcpy-win64-v5.0.1.zip runtime }
$oid='a98312a1427ca5c029e30009cc4a002e90affe9370e42b7f93a88ee6425c1278'
if (!(Test-Path runtime/webview.apk) -or (Hash 'runtime/webview.apk') -ne $oid) {
 $body=@{operation='download';transfers=@('basic');objects=@(@{oid=$oid;size=358908264})} | ConvertTo-Json -Depth 5
 $response=Invoke-RestMethod -Method Post -Uri 'https://review.lineageos.org/LineageOS/android_external_chromium-webview_prebuilt_x86_64.git/info/lfs/objects/batch' -ContentType 'application/vnd.git-lfs+json' -Body $body
 Fetch $response.objects[0].actions.download.href 'runtime/webview.apk' $oid
}
if (!(Test-Path runtime/webview-local.apk) -or (Get-Item runtime/webview-local.apk).Length -lt 1000000) {
 if (!$JavaHome -or !$ApkSignerJar) { throw 'Supply -JavaHome and -ApkSignerJar from a local Android SDK to sign the development WebView.' }
 $base='https://raw.githubusercontent.com/aosp-mirror/platform_build/master/target/product/security/'
 foreach ($file in @('testkey.pk8','testkey.x509.pem')) {
  & curl.exe --ssl-revoke-best-effort -fL ($base+$file) -o ('runtime/'+$file)
  if ($LASTEXITCODE) { throw "Cannot download public AOSP development key: $file" }
 }
 $pem=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'runtime/testkey.x509.pem'))
 $der=[Convert]::FromBase64String(($pem -replace '-----BEGIN CERTIFICATE-----','' -replace '-----END CERTIFICATE-----','' -replace '\s',''))
 $certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$der)
 $sha=[Security.Cryptography.SHA256]::Create()
 try { $fingerprint=([BitConverter]::ToString($sha.ComputeHash($certificate.RawData))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
 if ($fingerprint -ne 'a40da80a59d170caa950cf15c18c454d47a39b26989d8b640ecd745ba71bf5dc') { throw 'Unexpected AOSP test certificate' }
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=Join-Path $JavaHome 'bin/java.exe'
 $info.Arguments='-Xmx1200m --class-path "'+$ApkSignerJar+'" "'+$PSScriptRoot+'\tools\SignWebView.java" "'+$PSScriptRoot+'\runtime\webview.apk" "'+$PSScriptRoot+'\runtime\testkey.pk8" "'+$PSScriptRoot+'\runtime\testkey.x509.pem"'
 $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.CreateNoWindow=$true
 $process=[Diagnostics.Process]::Start($info); $errors=$process.StandardError.ReadToEndAsync()
 $temporary=Join-Path $PSScriptRoot ('runtime/webview-signed-'+[Guid]::NewGuid().ToString('N')+'.partial')
 $output=$null
 try {
  $output=[IO.File]::Create($temporary)
  $process.StandardOutput.BaseStream.CopyTo($output); $process.WaitForExit()
  if($process.ExitCode) {throw $errors.Result}
  $output.Dispose(); $output=$null
  Move-Item -LiteralPath $temporary -Destination (Join-Path $PSScriptRoot 'runtime/webview-local.apk') -Force
 }
 finally {
  if ($output) { $output.Dispose() }
  $process.Dispose()
  if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
 }
}
Write-Output 'Components prepared. WebView uses the PUBLIC AOSP test key for the local development image only; not a production release key.'
