[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) {
    if (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1) {
        throw 'Release destination must be empty; existing files will not be overwritten.'
    }
} else {
    New-Item -ItemType Directory -Path $Destination | Out-Null
}

$appDirectory = Join-Path $Destination 'app'
& (Join-Path $projectRoot 'publish-portable.ps1') -Destination $appDirectory -NoRestore:$NoRestore
$setup = Join-Path $Destination 'VEIL-MWBP-Setup.exe'
Move-Item -LiteralPath (Join-Path $appDirectory 'MouseWithoutBordersPlus.exe') -Destination $setup
Remove-Item -LiteralPath $appDirectory
foreach ($name in @('LICENSE', 'NOTICE.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $Destination
}
foreach ($name in @('InstallGuide.ko.txt', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Destination
}
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
($hash.Hash.ToLowerInvariant() + '  VEIL-MWBP-Setup.exe') | Set-Content -LiteralPath (Join-Path $Destination 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release prepared: $setup"
