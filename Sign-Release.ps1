#requires -Version 5.1
<#
.SYNOPSIS
Signs the built DroidNest.exe with a real Authenticode certificate and verifies the result.
.DESCRIPTION
Two modes are supported:
  * PFX file: -PfxPath <file> [-PfxPassword <password>]. If the password is omitted, the script
    prompts for it so it does not land in shell history.
  * Installed certificate (hardware token / HSM): -CertificateThumbprint <sha1 thumbprint>
    [-StoreLocation CurrentUser|LocalMachine]. The private key never leaves the token.
Only one mode may be used. The script never creates a self-signed certificate and fails when no
trusted certificate is available.
.EXAMPLE
.\Sign-Release.ps1 -PfxPath .\signing\DroidNest.pfx
.EXAMPLE
.\Sign-Release.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string]$PfxPath,

    [string]$PfxPassword,

    [string]$CertificateThumbprint,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser',

    [string]$ExePath,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot 'DroidNest.exe' }
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $ExePath)) { throw ('找不到客户端：' + $ExePath + '。请先运行 build.ps1。') }
if ($PfxPath -and $CertificateThumbprint) { throw '请二选一：-PfxPath 或 -CertificateThumbprint。' }
if (-not $PfxPath -and -not $CertificateThumbprint) { throw '需要一个真实的代码签名证书：-PfxPath 或 -CertificateThumbprint。' }

$certificateArguments = @()
if ($PfxPath) {
    $PfxPath = [IO.Path]::GetFullPath($PfxPath)
    if (-not (Test-Path -LiteralPath $PfxPath)) { throw "找不到代码签名证书：$PfxPath" }
    if (-not $PfxPassword) {
        $secure = Read-Host -AsSecureString '请输入 PFX 密码（输入不回显）'
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $PfxPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
    $certificateArguments = @('/f', $PfxPath, '/p', $PfxPassword)
}
else {
    $thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') { throw '证书指纹应为 40 位十六进制 SHA-1。' }
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', $StoreLocation)
    $certificate = $null
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $certificate = $store.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
    }
    finally { $store.Close() }
    if ($null -eq $certificate) { throw ('在 ' + $StoreLocation + ' 的个人证书存储中找不到该证书。请先用证书厂商的工具导入或插入令牌。') }
    if (-not $certificate.HasPrivateKey) { throw '该证书没有可用的私钥；请检查令牌/HSM 驱动或重新导入证书。' }
    $certificateArguments = @('/sha1', $thumbprint, '/s', 'My')
    if ($StoreLocation -eq 'LocalMachine') { $certificateArguments += '/sm' }
}

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -eq $signTool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidates = @()
    if (Test-Path -LiteralPath $kits) {
        $candidates = @(Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*\x64\signtool.exe' } | Sort-Object FullName -Descending)
    }
    if ($candidates.Count -eq 0) {
        throw '未找到 signtool.exe。请安装 Windows SDK 的签名工具，再把其 x64 目录加入 PATH。'
    }
    $signTool = $candidates[0]
}

Write-Host ('使用签名工具：' + $signTool.FullName)
$signArguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256') + $certificateArguments + @($ExePath)
& $signTool.FullName @signArguments
if ($LASTEXITCODE -ne 0) { throw 'signtool sign 失败；EXE 未被签名。' }
if ($PfxPassword) { $PfxPassword = $null }
& $signTool.FullName verify /pa /v $ExePath
if ($LASTEXITCODE -ne 0) { throw 'signtool verify 失败；不要发布此 EXE。' }

$signature = Get-AuthenticodeSignature -LiteralPath $ExePath
if ($signature.Status -ne 'Valid') { throw ('Authenticode 状态无效：' + $signature.Status) }
Write-Host ('已签名并通过验证：' + $ExePath)
Write-Host ('证书主题：' + $signature.SignerCertificate.Subject)
Write-Host ('证书 SHA-256 指纹：' + $signature.SignerCertificate.Thumbprint)
Write-Host '请把 EXE 的 SHA-256 一并写入发布校验文件；签名证明发布者，校验文件证明完整性，两者不能互相替代。'
