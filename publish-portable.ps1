[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'validate-icon.ps1')

$dotnet = Get-Command 'dotnet' -ErrorAction Stop

$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) {
    if (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1) {
        throw "Portable publish destination '$Destination' must be empty."
    }
} else {
    New-Item -ItemType Directory -Path $Destination | Out-Null
}

$projectPath = Join-Path $PSScriptRoot 'App\MouseWithoutBorders.csproj'
$runtimeIdentifier = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$arguments = @(
    'publish', $projectPath,
    '-c', $Configuration, '-r', $runtimeIdentifier,
    "-p:Platform=$Platform",
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    "-p:PathMap=$PSScriptRoot=/src",
    '-o', $Destination
)

if ($NoRestore) {
    $arguments += '--no-restore'
}

Write-Host "Publishing the $Platform single-file portable app"
& $dotnet.Source @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Portable publish failed (exit code $LASTEXITCODE)."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $Destination -File -Recurse)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'MouseWithoutBordersPlus.exe') {
    $publishedNames = $publishedFiles | ForEach-Object { $_.FullName.Substring($Destination.Length).TrimStart('\') }
    throw "Publish must contain exactly MouseWithoutBordersPlus.exe. Found: $($publishedNames -join ', ')"
}

if ($publishedFiles[0].Length -lt 1MB) {
    throw 'The portable executable is unexpectedly small and may not contain the .NET runtime.'
}

Write-Host "Single-file portable app published to '$($publishedFiles[0].FullName)' ($([Math]::Round($publishedFiles[0].Length / 1MB, 1)) MB)."
