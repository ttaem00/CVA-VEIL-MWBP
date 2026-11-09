$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Submit-StoreUpdate.ps1"

$script:StoreRequestHeaders = @{}
$script:Tag = 'mwb-v1.2.0'
$script:Hash = 'a' * 64
$script:OldUrl = $script:StoreReleasePrefix + 'mwb-v1.1.0/VEIL-MWBP-Setup.exe'
$script:NewUrl = $script:StoreReleasePrefix + $script:Tag + '/VEIL-MWBP-Store-Setup.exe'
$script:Calls = [Collections.Generic.Queue[hashtable]]::new()
$script:Cases = 0

function Reset-Case {
    $script:Calls.Clear()
    $script:Verified = 0
    $script:Waits = 0
    $script:RejectAsset = $false
}

function Add-Call {
    param([string]$Method, [string]$Path, [hashtable]$Data, [switch]$Ambiguous, [switch]$Interrupted)
    $script:Calls.Enqueue(@{
        Method = $Method; Path = $Path; Interrupted = $Interrupted
        Response = @{ isSuccess = if ($Ambiguous) { 'true' } else { $true }; responseData = $Data }
    })
}

function Add-Status {
    param([bool]$Ready = $true, [string]$Ongoing = '')
    Add-Call GET '/status' @{ isReady = $Ready; ongoingSubmissionId = $Ongoing }
}

function Add-Package {
    param([string]$Url = $script:OldUrl, [string]$Id = 'package-42', [bool]$SilentByDefault = $false)
    Add-Call GET '/packages' @{ packages = @(@{
        packageId = $Id; packageType = 'exe'; architectures = @('X64')
        isSilentInstall = $SilentByDefault; installerParameters = '--install --silent'
        genericDocUrl = 'https://example.org/install-errors'; packageUrl = $Url
    }) }
}

function Add-Preflight {
    Add-Status
    Add-Package
}

function Add-Commit {
    Add-Preflight
    Add-Package
    Add-Status
    Add-Call PATCH '/packages/package-42' @{}
    Add-Call POST '/packages/commit' @{ pollingUrl = 'https://example.org/never-follow' }
}

function Add-Readback {
    Add-Status
    Add-Package $script:NewUrl
    Add-Status
}

# The transport seam matches every request in order. No HTTP cmdlet is called.
function Invoke-StoreJson {
    param([string]$Stage, [hashtable]$Request)
    if ($script:Calls.Count -eq 0) { throw "Unexpected request: $Stage" }
    $expected = $script:Calls.Dequeue()
    if ($Request.Method -cne $expected.Method -or
        $Request.Uri -cne ($script:StoreProductPath + $expected.Path)) {
        throw "Out-of-order request: $Stage"
    }
    if ($expected.Method -ceq 'PATCH') {
        $body = ConvertFrom-Json -InputObject $Request.Body -AsHashtable
        if ($script:Verified -ne 1 -or $body.Count -ne 1 -or $body['packageUrl'] -cne $script:NewUrl) {
            throw 'PATCH must follow asset verification and change only packageUrl.'
        }
    } elseif ($Request.ContainsKey('Body')) {
        throw "Unexpected body: $Stage"
    }
    if ($expected.Interrupted) { throw 'Synthetic response lost after POST.' }
    $expected.Response
}

function Assert-StoreAssetHash {
    param([string]$Url, [string]$Sha256)
    if ($Url -cne $script:NewUrl -or $Sha256 -cne $script:Hash) { throw 'Incorrect public asset or hash.' }
    $script:Verified++
    if ($script:RejectAsset) { throw 'Synthetic asset hash mismatch.' }
}

function Start-Sleep {
    param([int]$Seconds)
    if ($Seconds -ne 5) { throw 'Unexpected polling delay.' }
    $script:Waits++
}

function Complete-Case {
    if ($script:Calls.Count -ne 0) { throw 'Expected API requests were not consumed.' }
    $script:Cases++
}

function Assert-Stopped {
    param([string]$Message)
    $reason = ''
    try { $null = Invoke-StoreUpdate $script:Tag $script:Hash } catch { $reason = $_.Exception.Message }
    if ($reason -notlike "*$Message*") { throw "Expected stop containing '$Message', got '$reason'." }
    Complete-Case
}

Reset-Case
Add-Preflight
$result = Invoke-StoreUpdate -OnlyCheck
if ($result.Status -cne 'DraftReady' -or $script:Verified -ne 0) { throw 'Read-only canary failed.' }
Complete-Case

Reset-Case
Add-Commit
Add-Status $false
Add-Readback
Add-Call POST '/submit' @{ submissionId = 'submission-42' }
$result = Invoke-StoreUpdate $script:Tag $script:Hash
if ($result.Status -cne 'SubmittedForCertification' -or $result.SubmissionId -cne 'submission-42' -or
    $result.AssetSha256 -cne $script:Hash -or $script:Waits -ne 1) { throw 'Submission receipt or polling result was incorrect.' }
Complete-Case

Reset-Case
Add-Status $false
Assert-Stopped 'draft is not ready'

Reset-Case
Add-Status -Ongoing 'existing-submission'
Assert-Stopped 'already in progress'

Reset-Case
Add-Call GET '/status' @{ isReady = $true; ongoingSubmissionId = $false }
Assert-Stopped 'state is ambiguous'

Reset-Case
Add-Status
Add-Call GET '/packages' @{ packages = @() }
Assert-Stopped 'exactly one existing package'

Reset-Case
Add-Status
Add-Package -SilentByDefault $true
Assert-Stopped 'Existing package must be an x64 EXE'

foreach ($url in @($script:NewUrl, ($script:StoreReleasePrefix + 'mwb-v1.3.0/VEIL-MWBP-Store-Setup.exe'))) {
    Reset-Case
    Add-Status
    Add-Package $url
    Assert-Stopped 'this or a newer version'
}

Reset-Case
Add-Preflight
$script:RejectAsset = $true
Assert-Stopped 'hash mismatch'

Reset-Case
Add-Preflight
Add-Package -Id 'concurrent-package'
Assert-Stopped 'draft changed'

Reset-Case
Add-Preflight
Add-Package
Add-Status -Ongoing 'concurrent-submission'
Assert-Stopped 'already in progress'

Reset-Case
Add-Commit
1..12 | ForEach-Object { Add-Status $false }
Assert-Stopped 'Timed out waiting'
if ($script:Waits -ne 11) { throw 'Polling did not stop at the configured bound.' }

Reset-Case
Add-Commit
Add-Status
Add-Package
Assert-Stopped 'identity, URL or readiness changed'

Reset-Case
Add-Commit
Add-Status
Add-Package $script:NewUrl -Id 'changed-after-commit'
Assert-Stopped 'identity, URL or readiness changed'

Reset-Case
Add-Commit
Add-Status
Add-Package $script:NewUrl
Add-Status $false
Assert-Stopped 'identity, URL or readiness changed'

Reset-Case
Add-Commit
Add-Readback
Add-Call POST '/submit' @{ submissionId = 'untrusted-id' } -Ambiguous
Assert-Stopped 'unsuccessful or ambiguous response'

Reset-Case
Add-Commit
Add-Readback
Add-Call POST '/submit' @{}
Assert-Stopped 'no valid ID'

Reset-Case
Add-Commit
Add-Readback
Add-Call POST '/submit' @{} -Interrupted
Assert-Stopped 'response lost after POST'

"Store update synthetic cases passed: $script:Cases"
