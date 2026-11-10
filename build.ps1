$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path $csc)) { throw 'Windows .NET Framework 4.8 compiler is required.' }
$refs = @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Net.Http.dll', 'System.Xml.Linq.dll', 'System.Xml.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'WPF\UIAutomationTypes.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$sources = @(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output /out:DroidNest.exe @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'DroidNest compilation failed.' }
Write-Output (Join-Path $PSScriptRoot 'DroidNest.exe')
