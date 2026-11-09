#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$file = Get-Item -LiteralPath $Path
$signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate) {
    throw 'A valid timestamped Authenticode signature is required.'
}
if ($signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=SignPath Foundation(?:,|$)') {
    throw 'The signed installer is not from the configured SignPath Foundation publisher.'
}
if ($Version -and $file.VersionInfo.ProductVersion -cne $Version) {
    throw 'Signed installer product version does not match the production release.'
}
(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
