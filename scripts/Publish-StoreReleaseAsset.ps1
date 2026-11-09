#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Commit,
    [Parameter(Mandatory)]
    [string] $Installer,
    [Parameter(Mandatory)]
    [string] $Checksum,
    [Parameter(Mandatory)]
    [string] $Manifest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$intent = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -AsHashtable
if ($intent.commit -cne $Commit -or -not $intent.tag -or [string] $intent.releaseId -notmatch '\A[0-9]+\z') {
    throw 'Invalid release identity in the signed artifact.'
}
$resolved = & "$PSScriptRoot/Resolve-StoreRelease.ps1" -Commit $Commit -ExpectedTag $intent.tag -ExpectedReleaseId $intent.releaseId
$hash = & "$PSScriptRoot/Get-StoreInstallerHash.ps1" -Path $Installer -Version $resolved.Version
if ((Get-Content -LiteralPath $Checksum -Raw).Trim() -cne "$hash  VEIL-MWBP-Setup.exe") {
    throw 'Signed artifact does not match the post-signing checksum.'
}

$asset = 'VEIL-MWBP-Store-Setup.exe'
$repo = $env:GITHUB_REPOSITORY
$existing = @($resolved.Release.assets | Where-Object { $_.name -ceq $asset })
if ($existing.Count -eq 0) {
    # The release-ID endpoint creates an asset; duplicates fail instead of replacing bytes.
    $uploadUri = "https://uploads.github.com/repos/$repo/releases/$($intent.releaseId)/assets?name=$asset"
    try {
        $null = Invoke-RestMethod -Uri $uploadUri -Method Post -InFile $Installer -ContentType 'application/octet-stream' `
            -Headers @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' } `
            -TimeoutSec 180 -MaximumRedirection 0
    } catch {
        throw 'GitHub did not confirm the asset upload. Inspect the exact release before retrying; never replace a published asset.'
    }
} elseif ($existing.Count -ne 1 -or $existing[0].state -cne 'uploaded') {
    throw 'Existing release asset is not ready. Inspect its ID and state in GitHub; no asset was replaced.'
}

$releaseId = & gh api "repos/$repo/releases/tags/$($resolved.Tag)" --jq .id
if ($LASTEXITCODE -ne 0 -or $releaseId -cne [string] $intent.releaseId) {
    throw 'The published release changed before public readback.'
}
$url = "https://github.com/$repo/releases/download/$($resolved.Tag)/$asset"
$download = Join-Path $env:RUNNER_TEMP 'mwbp-public-readback.exe'
Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec 120 -MaximumRedirection 5
$publicHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publicHash -cne $hash) {
    throw 'The public version URL has different bytes. Keep the asset unchanged and reconcile the signed run.'
}
"tag=$($resolved.Tag)" >> $env:GITHUB_OUTPUT
"sha256=$hash" >> $env:GITHUB_OUTPUT
Write-Host "Verified public signed installer for $($resolved.Tag): $hash"
