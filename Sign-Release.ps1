#requires -Version 5.1
<#
.SYNOPSIS
Signs the built DroidNest.exe with an Authenticode certificate and verifies the result.
.DESCRIPTION
Release signing needs a trusted code-signing certificate. This machine has none, so the script
cannot create a signature by itself; it refuses to continue without a real certificate instead of
generating a self-signed one. The PFX password is passed to signtool on the command line, so run
this on a trusted machine and rotate the certificate if the shell history is shared.
.EXAMPLE
.\Sign-Release.ps1 -PfxPath .\signing\DroidNest.pfx -PfxPassword '<password>'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$PfxPassword,

    [string]$ExePath = (Join-Path $PSScriptRoot 'DroidNest.exe'),

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$PfxPath = [IO.Path]::GetFullPath($PfxPath)
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $PfxPath)) { throw "找不到代码签名证书：$PfxPath" }
if (-not (Test-Path -LiteralPath $ExePath)) { throw ('找不到客户端：' + $ExePath + '。请先运行 build.ps1。') }

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -eq $signTool) {
    $candidates = @(Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\signtool.exe' } | Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) {
        throw '未找到 signtool.exe。请安装 Windows SDK 的签名工具，再把其 x64 目录加入 PATH。'
    }
    $signTool = $candidates[0]
}

Write-Host ('使用签名工具：' + $signTool.FullName)
& $signTool.FullName sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $TimestampUrl /td SHA256 $ExePath
if ($LASTEXITCODE -ne 0) { throw 'signtool sign 失败；EXE 未被签名。' }
& $signTool.FullName verify /pa /v $ExePath
if ($LASTEXITCODE -ne 0) { throw 'signtool verify 失败；不要发布此 EXE。' }

$signature = Get-AuthenticodeSignature -LiteralPath $ExePath
if ($signature.Status -ne 'Valid') { throw ('Authenticode 状态无效：' + $signature.Status) }
Write-Host ('已签名并通过验证：' + $ExePath)
Write-Host ('证书主题：' + $signature.SignerCertificate.Subject)
Write-Host ('证书 SHA-256：' + $signature.SignerCertificate.Thumbprint)
