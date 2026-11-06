#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [ValidateRange(1, 86400)][int]$MaxTotalSeconds = 600,
    [ValidateRange(1, 86400)][int]$ChildTimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$timer = [Diagnostics.Stopwatch]::StartNew()
$expectedRepo = 'ttaem00/CVA-VEIL-Mouse_Without_Border_Plus'
$allowedBootstrap = ''
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('/','\')
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('/','\')
if ($Root -ne $sourceRoot) { throw 'Root must be this script checkout; no other checkout is modified.' }
$git = (Get-Command git -ErrorAction Stop).Source
$gh = (Get-Command gh -ErrorAction Stop).Source
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source

function Invoke-Step([string]$Name, [string]$File, [string[]]$Arguments, [switch]$Quiet) {
    $remaining = $MaxTotalSeconds - $timer.Elapsed.TotalSeconds
    if ($remaining -le 0) { throw "Total validation timeout before: $Name" }
    Write-Host "[START] $Name"
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start: $Name" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $milliseconds = [int]([Math]::Min($remaining, $ChildTimeoutSeconds) * 1000)
        if (-not $process.WaitForExit($milliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Child validation timeout: $Name"
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if (-not $Quiet -or $process.ExitCode -ne 0) {
            if ($out) { Write-Host $out.TrimEnd() }
            if ($err) { Write-Host $err.TrimEnd() }
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed (exit $($process.ExitCode))." }
        Write-Host "[PASS] $Name"
        return $out
    } finally { $process.Dispose() }
}
function Git-Read([string]$Name, [string[]]$Arguments) {
    return (Invoke-Step $Name $git (@('--no-optional-locks', '-c', 'core.quotepath=false') + $Arguments) -Quiet).Trim()
}
function Assert-SourceClean {
    foreach ($arguments in @(@('diff','--name-only','--'), @('diff','--cached','--name-only','--'))) {
        $changed = Git-Read 'tracked source clean' $arguments
        if ($changed) { throw "Tracked changes must be committed before PR validation: $changed" }
    }
    $untracked = (Git-Read 'untracked source inventory' @('ls-files','--others','--exclude-standard','-z')).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    $bootstrap = @($untracked | Where-Object { $allowedBootstrap -and $_.StartsWith($allowedBootstrap, [StringComparison]::Ordinal) })
    $other = @($untracked | Where-Object { -not ($allowedBootstrap -and $_.StartsWith($allowedBootstrap, [StringComparison]::Ordinal)) })
    if ($other.Count) { throw "Untracked source is not validated: $(($other | Select-Object -First 10) -join ', ')" }
    Write-Host "[PASS] Source inventory: tracked clean; approved bootstrap untracked files=$($bootstrap.Count)"
    return $bootstrap.Count
}
function Read-Pr {
    $json = Invoke-Step 'current PR identity' $gh @('pr','view',$branch,'--repo',$expectedRepo,'--json','number,state,headRefName,headRefOid,baseRefName,baseRefOid') -Quiet
    $pr = $json | ConvertFrom-Json
    if ($pr.state -ne 'OPEN' -or $pr.headRefName -ne $branch -or $pr.baseRefName -ne 'main') { throw 'Expected the open current-branch PR targeting canonical main.' }
    $baseLine = Git-Read 'canonical main identity' @('ls-remote','--exit-code','origin','refs/heads/main')
    $baseOid = ($baseLine -split '\s+')[0]
    if ($pr.baseRefOid -ne $baseOid) { throw 'PR base differs from current canonical main; integrate additively and retry.' }
    $headLine = Git-Read 'canonical branch identity' @('ls-remote','--exit-code','origin',"refs/heads/$branch")
    if ($pr.headRefOid -ne ($headLine -split '\s+')[0]) { throw 'PR head differs from the current canonical branch.' }
    return $pr
}
try {
    $top = Git-Read 'checkout root' @('rev-parse','--show-toplevel')
    if ([IO.Path]::GetFullPath($top).TrimEnd('/','\') -ne $Root) { throw 'Root is not the Git top-level checkout.' }
    $branch = Git-Read 'checkout branch' @('symbolic-ref','--short','HEAD')
    if ($branch -notmatch '^codex/issue-[0-9]+-') { throw 'Use the owned issue branch; main and detached HEAD are not validation targets.' }
    $remote = Git-Read 'repository remote' @('remote','get-url','origin')
    if ($remote -notmatch ('^(https://github\.com/|git@github\.com:)' + [regex]::Escape($expectedRepo) + '(\.git)?$')) { throw 'Origin does not match this product repository.' }
    $bootstrapBefore = Assert-SourceClean
    $head = Git-Read 'local source identity' @('rev-parse','HEAD')
    $pr = Read-Pr
    if ($head -ne $pr.headRefOid) { throw 'Local HEAD differs from PR head; push or integrate normally first.' }
    # This exact fetch updates FETCH_HEAD only, never a branch, index, or worktree.
    $null = Invoke-Step 'fetch exact PR merge ref' $git @('fetch','--no-tags','--no-prune','--no-recurse-submodules','--no-auto-maintenance','origin',"refs/pull/$($pr.number)/merge")
    $merge = Git-Read 'PR merge identity' @('rev-parse','FETCH_HEAD')
    $parents = (Git-Read 'PR merge parents' @('rev-list','--parents','-n','1',$merge)) -split ' '
    if ($parents.Count -ne 3 -or $parents[1] -ne $pr.baseRefOid -or $parents[2] -ne $head) { throw 'Merge-ref parents differ from current base/head; integrate additively and retry.' }
    $tree = Git-Read 'source tree' @('rev-parse',"$head`^{tree}")
    if ($tree -ne (Git-Read 'merge tree' @('rev-parse',"$merge`^{tree}"))) { throw 'Local source tree differs from merge tree; integrate additively and retry.' }
    Write-Host "[PASS] Exact merge-tree equivalence: repo=$expectedRepo PR=$($pr.number) head=$head base=$($pr.baseRefOid) merge=$merge tree=$tree"
    if (-not $env:DOTNET_ROOT -or -not $env:DOTNET_CLI_HOME -or -not $env:NUGET_PACKAGES) { throw 'Provide process-local DOTNET_ROOT, DOTNET_CLI_HOME and NUGET_PACKAGES for the verified SDK.' }
    $sdk = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if ((Invoke-Step 'pinned SDK' $sdk @('--version') -Quiet).Trim() -ne '10.0.403') { throw 'Expected SDK 10.0.403 from the supplied local root.' }
    $null = Invoke-Step 'build and product tests' $pwsh @('-NoProfile','-File',(Join-Path $Root 'build.ps1'),'-RunTests')
    $package = Join-Path $Root ('artifacts/pr-validation/' + [guid]::NewGuid().ToString('N'))
    $null = Invoke-Step 'single-file release package' $pwsh @('-NoProfile','-File',(Join-Path $Root 'VEIL- MWBP/Build-Release.ps1'),'-Destination',$package)
    $bootstrapAfter = Assert-SourceClean
    if ($bootstrapAfter -ne $bootstrapBefore) { throw 'Bootstrap inventory changed during validation.' }
    if ((Git-Read 'final local head' @('rev-parse','HEAD')) -ne $head -or (Git-Read 'final branch' @('symbolic-ref','--short','HEAD')) -ne $branch) { throw 'Source branch/head changed during validation.' }
    $after = Read-Pr
    if ($after.number -ne $pr.number -or $after.headRefOid -ne $head -or $after.baseRefOid -ne $pr.baseRefOid) { throw 'PR head/base changed during validation; retry the new merge ref.' }
    [ordered]@{state='PASS';repository=$expectedRepo;pr=$pr.number;head=$head;base=$pr.baseRefOid;merge_ref=$merge;tree=$tree;bootstrap_untracked=$bootstrapAfter;elapsed_seconds=[Math]::Round($timer.Elapsed.TotalSeconds,1);actual_install_or_user_profile='not invoked'} | ConvertTo-Json -Compress | Write-Host
    exit 0
} catch {
    Write-Host "[FAIL] PR validation: $($_.Exception.Message)"
    exit 1
}