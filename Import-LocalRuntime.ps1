param([Parameter(Mandatory=$true)][string]$SdkPath)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SdkPath).Path
$destination = Join-Path $PSScriptRoot 'runtime\sdk'
$components = @('emulator','platform-tools','system-images\android-30\default\x86_64')
foreach ($component in $components) {
    if (!(Test-Path -LiteralPath (Join-Path $source $component))) { throw "Missing SDK component: $component" }
}
if (Test-Path -LiteralPath $destination) { throw 'Runtime destination already exists. Refusing to overwrite it.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($component in $components) {
    $target = Join-Path $destination $component
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    & robocopy (Join-Path $source $component) $target /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "Runtime copy failed for $component. Incomplete copy remains at $destination." }
}
Write-Output "Local runtime imported to $destination. No AVD or user data was copied."
Write-Output 'Local SDK use only; this import does not grant redistribution rights.'
exit 0
