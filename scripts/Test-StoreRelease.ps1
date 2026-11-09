#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ScratchRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path Env:GH_TOKEN) { throw 'Run these synthetic tests in a PowerShell process with GH_TOKEN unset.' }

$repo = 'ttaem00/CVA-VEIL-Mouse_Without_Border_Plus'
$commit = 'a' * 40
$version = '1.2.0'
$tag = "mwb-v$version"
$asset = 'VEIL-MWBP-Store-Setup.exe'
$signed = 'signed synthetic installer'
$signedHash = '9a0e1f446d210c44247082ccf7f8f198f3765e282998996e35d619d3c7af2c1d'
$unsignedHash = '64a1a7cd9b0186767ef8fbce0e37220a3fd87aca91d7632764408a5f64bc7a7d'
$publicUrl = "https://github.com/$repo/releases/download/$tag/$asset"
$uploadUrl = "https://uploads.github.com/repos/$repo/releases/42/assets?name=$asset"
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$installer = Join-Path $scratch 'VEIL-MWBP-Setup.exe'
$checksum = Join-Path $scratch 'SHA256SUMS.txt'
$manifest = Join-Path $scratch 'manifest.json'
$storeReleaseTest = @{
    Repo = $repo; Commit = $commit; Tag = $tag; Scratch = $scratch; Installer = $installer
    SignedHash = $signedHash; Asset = $asset; PublicUrl = $publicUrl; UploadUrl = $uploadUrl
}
$script:passed = 0

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Write-Manifest([string] $ReleaseId) {
    @{ tag = $tag; releaseId = $ReleaseId; commit = $commit } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $manifest -Encoding ascii
}

function Test-Scenario([string] $Name, [scriptblock] $Action) {
    $script:fixture = @{
        Ancestor = $true
        SourceVersion = $version
        Channel = 'stable'
        TagCommit = $commit
        ProductVersion = $version
        SignatureStatus = 'Valid'
        Timestamp = [pscustomobject] @{}
        Signer = 'CN=SignPath Foundation, O=SignPath Foundation'
        Release = @{
            id = 42
            tag_name = $tag
            draft = $false
            prerelease = $false
            published_at = '2026-01-01T00:00:00Z'
            immutable = $false
            assets = @()
        }
        ReadbackReleaseId = '42'
        PublicBytes = $signed
        ApiReads = 0
        ApiResponses = @{}
        Uploads = 0
        UploadUnconfirmed = $false
        Downloads = 0
    }
    $env:GITHUB_REPOSITORY = $repo
    $env:GITHUB_OUTPUT = Join-Path $scratch "output-$script:passed.txt"
    [IO.File]::WriteAllText($installer, $signed)
    Set-Content -LiteralPath $checksum -Value "$signedHash  VEIL-MWBP-Setup.exe" -Encoding ascii
    Write-Manifest '42'
    try {
        & $Action
    } catch {
        throw ('{0}: {1}' -f $Name, $_.Exception.Message)
    }
    $script:passed++
    Write-Host "[PASS] $Name"
}

function Reject([string] $Message, [scriptblock] $Action) {
    $reason = ''
    try { $null = & $Action } catch { $reason = $_.Exception.Message }
    Assert ($reason.Contains($Message, [StringComparison]::OrdinalIgnoreCase)) "Expected '$Message'; got '$reason'."
}

function git {
    Assert ($args.Count -ge 3 -and $args[0] -ceq '-C') 'Unexpected git invocation.'
    $global:LASTEXITCODE = 0
    switch -CaseSensitive ($args[2]) {
        'merge-base' {
            Assert (($args[3..($args.Count - 1)] -join ' ') -ceq "--is-ancestor $($storeReleaseTest.Commit) refs/remotes/origin/main") 'Unexpected ancestry query.'
            if (-not $fixture.Ancestor) { $global:LASTEXITCODE = 1 }
        }
        'show' {
            Assert ($args.Count -eq 4 -and $args[3] -ceq "$($storeReleaseTest.Commit):Directory.Build.props") 'Wrong source metadata query.'
            "<Project><PropertyGroup><Version>$($fixture.SourceVersion)</Version><VersionChannel>$($fixture.Channel)</VersionChannel></PropertyGroup></Project>"
        }
        'rev-parse' {
            Assert (($args[3..($args.Count - 1)] -join ' ') -ceq "--verify refs/tags/mwb-v$($fixture.SourceVersion)^{commit}") 'Wrong tag resolution.'
            $fixture.TagCommit
        }
        default { throw "Unexpected git command: $($args[2])" }
    }
}

function gh {
    $request = $args -join ' '
    $releaseRequest = "api repos/$($storeReleaseTest.Repo)/releases/tags/$($storeReleaseTest.Tag)"
    $fixture.ApiReads++
    $global:LASTEXITCODE = 0
    if ($request -ceq $releaseRequest) { return $fixture.Release | ConvertTo-Json -Depth 5 -Compress }
    if ($request -ceq "$releaseRequest --jq .id") { return $fixture.ReadbackReleaseId }
    Assert ($fixture.ApiResponses.ContainsKey($request)) "Unexpected gh invocation: $request"
    $fixture.ApiResponses[$request]
}

function Get-Item {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath)

    Assert ($LiteralPath -ceq $storeReleaseTest.Installer) 'Unexpected synthetic file metadata path.'
    [pscustomobject] @{ FullName = $storeReleaseTest.Installer; VersionInfo = @{ ProductVersion = $fixture.ProductVersion } }
}

function Get-AuthenticodeSignature {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath)

    Assert ($LiteralPath -ceq $storeReleaseTest.Installer) 'Unexpected synthetic signature path.'
    [pscustomobject] @{
        Status = $fixture.SignatureStatus
        TimeStamperCertificate = $fixture.Timestamp
        SignerCertificate = @{ Subject = $fixture.Signer }
    }
}

function Invoke-RestMethod {
    [CmdletBinding()]
    param([string] $Uri, [string] $Method, [string] $InFile, [string] $ContentType,
          [hashtable] $Headers, [int] $TimeoutSec, [int] $MaximumRedirection)

    Assert ($Uri -ceq $storeReleaseTest.UploadUrl -and $Method -eq 'Post') 'Upload did not target the exact release ID and new asset name.'
    Assert ($ContentType -ceq 'application/octet-stream') 'Upload is not the raw installer bytes.'
    Assert ($PSBoundParameters.ContainsKey('MaximumRedirection') -and $MaximumRedirection -eq 0) 'Authenticated upload may redirect.'
    Assert ($Headers.Authorization -ceq 'Bearer synthetic-test-token') 'Unexpected upload authentication.'
    Assert ((Get-FileHash -LiteralPath $InFile).Hash -eq $storeReleaseTest.SignedHash) 'Upload payload is not the signed installer.'
    $fixture.Uploads++
    if ($fixture.UploadUnconfirmed) { throw 'Synthetic connection loss after the POST.' }
    @{ name = $storeReleaseTest.Asset; id = 500 }
}

function Invoke-WebRequest {
    [CmdletBinding()]
    param([string] $Uri, [string] $OutFile, [int] $TimeoutSec, [int] $MaximumRedirection)

    Assert ($Uri -ceq $storeReleaseTest.PublicUrl) 'Readback did not use the public versioned URL.'
    Assert ((Split-Path -Parent $OutFile) -ceq $storeReleaseTest.Scratch) 'Readback destination left the fixture directory.'
    $fixture.Downloads++
    [IO.File]::WriteAllText($OutFile, $fixture.PublicBytes)
}

function Set-RunFixture([string] $Kind, [string] $Conclusion = 'success', [string] $Artifact = 'valid', [int] $EventWorkflowId = 71) {
    $workflow, $job, $prefix = if ($Kind -eq 'Intent') {
        'store-release-signal.yml', 'intent', 'mwbp-release-intent'
    } else {
        'store-signing.yml', 'sign', 'mwbp-signed'
    }
    @{ workflow_run = @{ id = 100; run_attempt = 2; workflow_id = $EventWorkflowId } } |
        ConvertTo-Json -Depth 3 -Compress | Set-Content -LiteralPath $env:GITHUB_EVENT_PATH -Encoding ascii
    $fixture.ApiResponses["api repos/$repo/actions/workflows/$workflow --jq .id"] = '71'
    $fixture.ApiResponses["api repos/$repo/actions/runs/100/attempts/2/jobs"] =
        @{ jobs = @(@{ name = $job; conclusion = $Conclusion }) } | ConvertTo-Json -Depth 4 -Compress
    if ($Conclusion -eq 'skipped') { return }
    $artifacts = @(@{ name = "$prefix-100-1"; id = 501; expired = $false })
    if ($Artifact -ne 'missing') {
        $artifacts += @{ name = "$prefix-100-2"; id = 502; expired = ($Artifact -eq 'expired') }
    }
    $fixture.ApiResponses["api repos/$repo/actions/runs/100/artifacts?name=$prefix-100-2"] =
        @{ artifacts = $artifacts } | ConvertTo-Json -Depth 4 -Compress
}

$resolve = { & "$PSScriptRoot/Resolve-StoreRelease.ps1" -Commit $commit -ExpectedTag $tag -ExpectedReleaseId '42' }
$hashInstaller = { & "$PSScriptRoot/Get-StoreInstallerHash.ps1" -Path $installer -Version $version }
$publish = { & "$PSScriptRoot/Publish-StoreReleaseAsset.ps1" -Commit $commit -Installer $installer -Checksum $checksum -Manifest $manifest }
$originalRepo = $env:GITHUB_REPOSITORY
$originalTemp = $env:RUNNER_TEMP
$originalOutput = $env:GITHUB_OUTPUT
$originalEventPath = $env:GITHUB_EVENT_PATH
try {
    $env:RUNNER_TEMP = $scratch
    $env:GITHUB_EVENT_PATH = Join-Path $scratch 'event.json'
    $env:GH_TOKEN = 'synthetic-test-token'

    Test-Scenario 'Stable release resolves to the exact source, tag and ID' {
        $result = & $resolve
        Assert ($result.Tag -ceq $tag -and $result.Version -ceq $version -and $result.Release.id -eq 42) 'Release identity was not preserved.'
        Assert ($fixture.ApiReads -eq 1) 'Expected one release query.'
    }
    Test-Scenario 'Commit outside reviewed main is rejected before release lookup' {
        $fixture.Ancestor = $false
        Reject 'not in reviewed main' $resolve
        Assert ($fixture.ApiReads -eq 0) 'A non-main commit reached GitHub lookup.'
    }
    Test-Scenario 'Release candidate source is rejected before release lookup' {
        $fixture.SourceVersion = '1.2.0-rc.1'
        $fixture.Channel = 'preview'
        Reject 'stable MAJOR.MINOR.PATCH' $resolve
        Assert ($fixture.ApiReads -eq 0) 'A release candidate reached GitHub lookup.'
    }
    Test-Scenario 'Event tag must match source version' {
        Reject 'tag does not match its source version' {
            & "$PSScriptRoot/Resolve-StoreRelease.ps1" -Commit $commit -ExpectedTag 'mwb-v1.2.1'
        }
        Assert ($fixture.ApiReads -eq 0) 'A mismatched tag reached GitHub lookup.'
    }
    Test-Scenario 'Resolved tag must peel to the expected commit' {
        $fixture.TagCommit = 'b' * 40
        Reject 'tag does not identify' $resolve
        Assert ($fixture.ApiReads -eq 0) 'A moved tag reached GitHub lookup.'
    }
    foreach ($state in @('draft', 'prerelease')) {
        Test-Scenario "A $state release cannot be used" {
            $fixture.Release[$state] = $true
            Reject 'published production release' $resolve
        }
    }
    Test-Scenario 'Immutable published release cannot receive a late asset' {
        $fixture.Release.immutable = $true
        Reject 'immutable release' $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 0) 'An immutable release reached asset operations.'
    }
    Test-Scenario 'Manifest from a replaced release is rejected' {
        Write-Manifest '41'
        Reject 'published production release' $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 0) 'An old release ID reached asset operations.'
    }
    Test-Scenario 'Hash is computed from the timestamped signed bytes' {
        $actual = & $hashInstaller
        Assert ($actual -ceq $signedHash -and $actual -cne $unsignedHash) 'Wrong signed-byte SHA-256.'
    }
    Test-Scenario 'Hash rejects an invalid signature' {
        $fixture.SignatureStatus = 'NotSigned'
        Reject 'valid timestamped Authenticode' $hashInstaller
    }
    Test-Scenario 'Hash rejects a missing timestamp' {
        $fixture.Timestamp = $null
        Reject 'valid timestamped Authenticode' $hashInstaller
    }
    Test-Scenario 'Hash rejects the wrong signer' {
        $fixture.Signer = 'CN=Other Publisher'
        Reject 'configured SignPath Foundation' $hashInstaller
    }
    Test-Scenario 'Hash rejects the wrong embedded product version' {
        $fixture.ProductVersion = '1.2.1'
        Reject 'product version does not match' $hashInstaller
    }
    Test-Scenario 'Unsigned checksum cannot authorize asset upload' {
        Set-Content -LiteralPath $checksum -Value "$unsignedHash  VEIL-MWBP-Setup.exe" -Encoding ascii
        Reject 'post-signing checksum' $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 0) 'A pre-signing checksum reached asset operations.'
    }
    Test-Scenario 'New signed asset is uploaded once and read back anonymously' {
        $null = & $publish
        Assert ($fixture.Uploads -eq 1 -and $fixture.Downloads -eq 1) 'Expected one upload and public readback.'
        Assert (((Get-Content -LiteralPath $env:GITHUB_OUTPUT) -join '|') -ceq "tag=$tag|sha256=$signedHash") 'Output does not identify the verified tag and hash.'
    }
    Test-Scenario 'Matching existing asset is reused without upload' {
        $fixture.Release.assets = @(@{ name = $asset; id = 500; state = 'uploaded' })
        $null = & $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 1) 'An existing asset was not verified without upload.'
    }
    Test-Scenario 'Different public bytes stop without replacing the existing asset' {
        $fixture.Release.assets = @(@{ name = $asset; id = 500; state = 'uploaded' })
        $fixture.PublicBytes = 'different public installer'
        Reject 'different bytes' $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 1) 'A conflicting existing asset was not preserved.'
        Assert (-not (Test-Path -LiteralPath $env:GITHUB_OUTPUT)) 'Unverified bytes were exposed as workflow output.'
    }
    Test-Scenario 'Incomplete existing asset requires inspection' {
        $fixture.Release.assets = @(@{ name = $asset; id = 500; state = 'starter' })
        Reject 'Existing release asset is not ready' $publish
        Assert ($fixture.Uploads -eq 0 -and $fixture.Downloads -eq 0) 'Incomplete asset was overwritten or downloaded.'
    }
    Test-Scenario 'Ambiguous upload is not repeated' {
        $fixture.UploadUnconfirmed = $true
        Reject 'did not confirm the asset upload' $publish
        Assert ($fixture.Uploads -eq 1 -and $fixture.Downloads -eq 0) 'An unconfirmed upload reached another asset operation.'
        Assert (-not (Test-Path -LiteralPath $env:GITHUB_OUTPUT)) 'An unconfirmed upload was exposed as workflow output.'
    }
    Test-Scenario 'Release ID changes before public readback' {
        $fixture.ReadbackReleaseId = '43'
        Reject 'release' $publish
        Assert ($fixture.Uploads -eq 1 -and $fixture.Downloads -eq 0) 'A replaced release reached public readback.'
        Assert (-not (Test-Path -LiteralPath $env:GITHUB_OUTPUT)) 'A replaced release was exposed as workflow output.'
    }
    foreach ($kind in @('Intent', 'Signed')) {
        Test-Scenario "Select the exact $kind artifact for the upstream run attempt" {
            Set-RunFixture $kind
            $selected = & "$PSScriptRoot/Get-StoreRunArtifact.ps1" -Kind $kind
            Assert ($selected -ceq '502' -and $fixture.ApiReads -eq 3) 'Artifact selection did not preserve exact run identity.'
        }
    }
    $signedArtifact = { & "$PSScriptRoot/Get-StoreRunArtifact.ps1" -Kind Signed }
    Test-Scenario 'Skipped upstream job does not query artifacts' {
        Set-RunFixture Signed -Conclusion skipped
        Assert ($null -eq (& $signedArtifact) -and $fixture.ApiReads -eq 2) 'A skipped job queried or selected an artifact.'
    }
    foreach ($state in @('missing', 'expired')) {
        Test-Scenario "Successful job rejects an unavailable artifact ($state)" {
            Set-RunFixture Signed -Artifact $state
            Reject 'one unexpired artifact' $signedArtifact
        }
    }
    Test-Scenario 'Wrong upstream workflow is rejected before job lookup' {
        Set-RunFixture Signed -EventWorkflowId 72
        Reject 'Unexpected upstream workflow identity' $signedArtifact
        Assert ($fixture.ApiReads -eq 1) 'A different workflow reached attempt lookup.'
    }
    Write-Host "[PASS] $script:passed synthetic Store release scenarios."
} finally {
    $env:GITHUB_REPOSITORY = $originalRepo
    $env:RUNNER_TEMP = $originalTemp
    $env:GITHUB_OUTPUT = $originalOutput
    $env:GITHUB_EVENT_PATH = $originalEventPath
    $env:GH_TOKEN = $null
}
