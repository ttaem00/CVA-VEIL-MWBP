#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Intent', 'Signed')]
    [string] $Kind
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = 'ttaem00/CVA-VEIL-Mouse_Without_Border_Plus'
if ($env:GITHUB_REPOSITORY -ne $repo) { throw 'Unexpected repository.' }
$run = (Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json).workflow_run
$id = [string] $run.id
$attempt = [string] $run.run_attempt
if ($id -notmatch '\A[0-9]+\z' -or $attempt -notmatch '\A[0-9]+\z') {
    throw 'Unexpected upstream run identity.'
}
if ($Kind -eq 'Intent') {
    $workflow = 'store-release-signal.yml'; $job = 'intent'; $prefix = 'mwbp-release-intent'
} else {
    $workflow = 'store-signing.yml'; $job = 'sign'; $prefix = 'mwbp-signed'
}

$workflowId = & gh api "repos/$repo/actions/workflows/$workflow" --jq .id
if ($LASTEXITCODE -ne 0 -or $workflowId -cne [string] $run.workflow_id) {
    throw 'Unexpected upstream workflow identity.'
}
$rawJobs = & gh api "repos/$repo/actions/runs/$id/attempts/$attempt/jobs"
if ($LASTEXITCODE -ne 0) { throw 'Could not read upstream job status.' }
$jobs = @((($rawJobs | ConvertFrom-Json).jobs) | Where-Object { $_.name -ceq $job })
if ($jobs.Count -ne 1) { throw 'Expected one upstream job in the exact attempt.' }
if ($jobs[0].conclusion -ceq 'skipped') { return }
if ($jobs[0].conclusion -cne 'success') { throw 'Upstream job did not succeed.' }

$name = "$prefix-$id-$attempt"
$raw = & gh api "repos/$repo/actions/runs/$id/artifacts?name=$name"
if ($LASTEXITCODE -ne 0) { throw 'Could not read artifacts from the exact upstream run.' }
$artifacts = @((($raw | ConvertFrom-Json).artifacts) | Where-Object { $_.name -ceq $name -and -not $_.expired })
if ($artifacts.Count -ne 1 -or [string] $artifacts[0].id -notmatch '\A[0-9]+\z') {
    throw 'A successful job must have exactly one unexpired artifact for this attempt.'
}
[string] $artifacts[0].id
