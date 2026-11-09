#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A[0-9a-f]{40}\z')]
    [string] $Commit,
    [string] $ExpectedTag,
    [string] $ExpectedReleaseId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = 'ttaem00/CVA-VEIL-Mouse_Without_Border_Plus'
$root = Split-Path -Parent $PSScriptRoot
if ($env:GITHUB_REPOSITORY -ne $repo) { throw 'Only the canonical MWBP repository can release to Store.' }

& git -C $root merge-base --is-ancestor $Commit refs/remotes/origin/main
if ($LASTEXITCODE -ne 0) { throw 'Release commit is not in reviewed main history.' }
$props = & git -C $root show "${Commit}:Directory.Build.props"
if ($LASTEXITCODE -ne 0) { throw 'Could not read version metadata from the release commit.' }
[xml] $project = $props -join "`n"
$version = [string] $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$channel = [string] $project.SelectSingleNode('/Project/PropertyGroup/VersionChannel').InnerText
if ($channel -cne 'stable' -or $version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') {
    throw 'Store automation requires a stable MAJOR.MINOR.PATCH source version.'
}
$tag = "mwb-v$version"
if ($ExpectedTag -and $ExpectedTag -cne $tag) { throw 'Release tag does not match its source version.' }
$tagCommit = & git -C $root rev-parse --verify "refs/tags/$tag^{commit}"
if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $Commit) { throw 'Release tag does not identify the workflow commit.' }

$response = & gh api "repos/$repo/releases/tags/$tag"
if ($LASTEXITCODE -ne 0) { throw 'Could not read the published GitHub release.' }
$release = $response | ConvertFrom-Json -AsHashtable
if ($release.tag_name -cne $tag -or $release.draft -ne $false -or
    $release.prerelease -ne $false -or -not $release.published_at -or
    ($ExpectedReleaseId -and [string] $release.id -cne $ExpectedReleaseId)) {
    throw 'Expected the same published production release.'
}
if ($release.immutable -ne $false) {
    throw 'This published release cannot receive a new asset. An immutable release needs its signed asset attached before publication.'
}

[pscustomobject] @{ Tag = $tag; Version = $version; Release = $release }
