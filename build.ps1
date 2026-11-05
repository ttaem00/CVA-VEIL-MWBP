[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',
    [switch] $RunTests,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'validate-icon.ps1')
$dotnet = Get-Command 'dotnet' -ErrorAction Stop
$project = Join-Path $PSScriptRoot 'MouseWithoutBorders.UnitTests/MouseWithoutBorders.UnitTests.csproj'
$arguments = @('build', $project, '-c', $Configuration, "-p:Platform=$Platform")
if ($NoRestore) { $arguments += '--no-restore' }
& $dotnet.Source @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }
if ($RunTests) {
    & $dotnet.Source test $project --no-build --no-restore -c $Configuration "-p:Platform=$Platform"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)." }
}
