[CmdletBinding()]
param(
    [ValidatePattern('\Amwb-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z', Options = 'None')]
    [string]$ReleaseTag,
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$AssetSha256,
    [switch]$CheckOnly
)

$script:StoreProductPath = 'https://api.store.microsoft.com/submission/v1/product/f40c2d4a-951e-4d8b-b059-d9eaeabf89bb'
$script:StoreReleasePrefix = 'https://github.com/ttaem00/CVA-VEIL-Mouse_Without_Border_Plus/releases/download/'
$script:StoreVersionPattern = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'

function Invoke-StoreJson {
    param([string]$Stage, [hashtable]$Request)
    try {
        $response = Invoke-WebRequest @Request -MaximumRedirection 0 -MaximumRetryCount 0 -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 30 -SkipHttpErrorCheck -ErrorAction Stop -Verbose:$false -Debug:$false
    } catch {
        throw "$Stage request was interrupted. Confirm remote state before retrying."
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "$Stage returned HTTP $($response.StatusCode). Confirm remote state before retrying."
    }
    try {
        $json = ConvertFrom-Json -InputObject $response.Content -AsHashtable -NoEnumerate -ErrorAction Stop
    } catch {
        throw "$Stage returned invalid JSON. Confirm remote state before retrying."
    }
    if ($json -isnot [System.Collections.IDictionary]) { throw "$Stage returned an unexpected response." }
    $json
}

function Get-StoreRequestHeaders {
    # https://docs.github.com/en/actions/reference/security/oidc
    # https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
    $tenant = [guid]::Empty
    $client = [guid]::Empty
    $seller = $env:MWBP_STORE_SELLER_ID
    if (-not [guid]::TryParseExact($env:MWBP_STORE_TENANT_ID, 'D', [ref]$tenant) -or $tenant -eq [guid]::Empty -or
        -not [guid]::TryParseExact($env:MWBP_STORE_CLIENT_ID, 'D', [ref]$client) -or $client -eq [guid]::Empty -or
        $seller -cnotmatch '\A[A-Za-z0-9_-]{1,128}\z') {
        throw 'Set the Store tenant, client and seller IDs in the GitHub environment.'
    }
    $oidcUri = $null
    if (-not [uri]::TryCreate($env:ACTIONS_ID_TOKEN_REQUEST_URL, [UriKind]::Absolute, [ref]$oidcUri) -or
        $oidcUri.Scheme -cne 'https' -or $oidcUri.Port -ne 443 -or $oidcUri.UserInfo -or $oidcUri.Fragment -or
        $oidcUri.Host -notin @('pipelines.actions.githubusercontent.com', 'token.actions.githubusercontent.com') -or
        [string]::IsNullOrWhiteSpace($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN)) {
        throw 'A GitHub-hosted job with OIDC id-token: write is required.'
    }
    $separator = if ($oidcUri.Query) { '&' } else { '?' }
    $audience = [uri]::EscapeDataString('api://AzureADTokenExchange')
    $oidc = Invoke-StoreJson 'GitHub OIDC' @{
        Uri = "$oidcUri$separator" + "audience=$audience"
        Headers = @{ Authorization = "Bearer $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN" }
    }
    if ($oidc['value'] -isnot [string] -or [string]::IsNullOrWhiteSpace($oidc['value'])) {
        throw 'GitHub OIDC did not return an assertion.'
    }
    $token = Invoke-StoreJson 'Entra token exchange' @{
        Uri = "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token"
        Method = 'POST'
        ContentType = 'application/x-www-form-urlencoded'
        Body = @{
            grant_type = 'client_credentials'
            client_id = $client.ToString()
            scope = 'https://api.store.microsoft.com/.default'
            client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
            client_assertion = $oidc['value']
        }
    }
    if ($token['access_token'] -isnot [string] -or [string]::IsNullOrWhiteSpace($token['access_token'])) {
        throw 'Entra did not return a Store access token.'
    }
    @{ Authorization = "Bearer $($token['access_token'])"; 'X-Seller-Account-Id' = $seller }
}

function Invoke-StoreApi {
    param([string]$Method, [string]$Path, [hashtable]$Body)
    # https://learn.microsoft.com/en-us/windows/apps/publish/store-submission-api
    $request = @{ Uri = "$script:StoreProductPath$Path"; Method = $Method; Headers = $script:StoreRequestHeaders }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Compress
    }
    $response = Invoke-StoreJson "Store $Method $Path" $request
    $data = $response['responseData']
    if ($response['isSuccess'] -isnot [bool] -or -not $response['isSuccess'] -or
        ($null -ne $response['errors'] -and @($response['errors']).Count -ne 0) -or
        $data -isnot [System.Collections.IDictionary]) {
        throw "Store $Method $Path returned an unsuccessful or ambiguous response. Inspect the current draft or submission before retrying."
    }
    $ongoing = $data['ongoingSubmissionId']
    if ($null -ne $ongoing -and ($ongoing -isnot [string] -or $ongoing.Length -ne 0)) {
        throw 'A Store submission is already in progress or its state is ambiguous.'
    }
    $data
}

function Get-StoreReady {
    $status = Invoke-StoreApi GET '/status'
    if ($status['isReady'] -isnot [bool]) { throw 'Store draft readiness is ambiguous.' }
    $status['isReady']
}

function Get-StorePackage {
    $data = Invoke-StoreApi GET '/packages'
    $packages = $data['packages']
    if ($packages -isnot [array] -or $packages.Count -ne 1 -or $packages[0] -isnot [System.Collections.IDictionary]) {
        throw 'Store updates require exactly one existing package. Complete the initial submission in Partner Center.'
    }
    $package = $packages[0]
    $architectures = $package['architectures']
    $docUri = $null
    if ($package['packageId'] -isnot [string] -or $package['packageId'] -cnotmatch '\A[A-Za-z0-9_-]{1,128}\z' -or
        $package['packageType'] -isnot [string] -or $package['packageType'] -ine 'exe' -or
        $architectures -isnot [array] -or $architectures.Count -ne 1 -or $architectures[0] -isnot [string] -or $architectures[0] -ine 'x64' -or
        $package['isSilentInstall'] -isnot [bool] -or $package['isSilentInstall'] -or
        $package['installerParameters'] -isnot [string] -or $package['installerParameters'] -cne '--install --silent' -or
        $package['genericDocUrl'] -isnot [string] -or
        -not [uri]::TryCreate($package['genericDocUrl'], [UriKind]::Absolute, [ref]$docUri) -or
        $docUri.Scheme -cne 'https' -or $docUri.UserInfo -or $docUri.IsLoopback -or
        $docUri.HostNameType -ne [UriHostNameType]::Dns -or $docUri.Host -notmatch '\.') {
        throw 'Existing package must be an x64 EXE with --install --silent and a public HTTPS error-code documentation URL.'
    }
    $url = $package['packageUrl']
    $previous = $null
    $pattern = '\A' + [regex]::Escape($script:StoreReleasePrefix) + "mwb-v(?<version>$script:StoreVersionPattern)/VEIL-MWBP-(Store-)?Setup\.exe\z"
    if ($url -isnot [string] -or $url -cnotmatch $pattern -or -not [version]::TryParse($Matches['version'], [ref]$previous)) {
        throw 'The existing Store package URL must identify a canonical stable MWBP release.'
    }
    [pscustomobject]@{ Id = $package['packageId']; Url = $url; Version = $previous; DocUrl = $package['genericDocUrl'] }
}

function Assert-StoreAssetHash {
    param([string]$Url, [string]$Sha256)
    if (-not $env:RUNNER_TEMP -or -not [IO.Path]::IsPathFullyQualified($env:RUNNER_TEMP) -or
        -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
        throw 'The GitHub runner temporary directory is required for asset verification.'
    }
    $path = Join-Path $env:RUNNER_TEMP "mwbp-store-$([guid]::NewGuid().ToString('N')).exe"
    try {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $path -MaximumRedirection 5 -MaximumRetryCount 0 -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 120 -ErrorAction Stop -Verbose:$false -Debug:$false
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
        } catch {
            throw 'Could not download and hash the public Store release asset.'
        }
        if ($actual -ine $Sha256) { throw 'The public Store release asset SHA-256 differs from the verified signed output.' }
    } finally {
        [IO.File]::Delete($path)
    }
}

function Invoke-StoreUpdate {
    param([string]$Tag, [string]$Sha256, [switch]$OnlyCheck)
    if (-not (Get-StoreReady)) { throw 'The current Store draft is not ready.' }
    $existing = Get-StorePackage
    if ($OnlyCheck) { return [pscustomobject]@{ Status = 'DraftReady'; PackageId = $existing.Id } }

    $version = [version]$Tag.Substring(5)
    if ($existing.Version -ge $version) { throw 'The Store package already targets this or a newer version. Inspect its submission status.' }
    $assetUrl = "$script:StoreReleasePrefix$Tag/VEIL-MWBP-Store-Setup.exe"
    Assert-StoreAssetHash $assetUrl $Sha256

    $current = Get-StorePackage
    if ($current.Id -cne $existing.Id -or $current.Url -cne $existing.Url -or $current.DocUrl -cne $existing.DocUrl -or
        -not (Get-StoreReady)) {
        throw 'The Store draft changed while checking the release asset.'
    }

    $null = Invoke-StoreApi PATCH "/packages/$($existing.Id)" @{ packageUrl = $assetUrl }
    $null = Invoke-StoreApi POST '/packages/commit'
    for ($poll = 1; $poll -le 12; $poll++) {
        if (Get-StoreReady) { break }
        if ($poll -eq 12) { throw 'Timed out waiting for Store package readiness. Inspect the draft before retrying.' }
        Start-Sleep -Seconds 5
    }

    $confirmed = Get-StorePackage
    if ($confirmed.Id -cne $existing.Id -or $confirmed.Url -cne $assetUrl -or $confirmed.DocUrl -cne $existing.DocUrl -or
        -not (Get-StoreReady)) {
        throw 'Store package identity, URL or readiness changed before submission.'
    }
    $submission = Invoke-StoreApi POST '/submit'
    $id = $submission['submissionId']
    if ($id -isnot [string] -or $id -cnotmatch '\A[A-Za-z0-9_-]{1,128}\z') {
        throw 'Store submission response has no valid ID. Inspect Partner Center before retrying.'
    }
    [pscustomobject]@{
        Status = 'SubmittedForCertification'
        SubmissionId = $id
        ReleaseTag = $Tag
        PackageUrl = $assetUrl
        AssetSha256 = $Sha256.ToLowerInvariant()
    }
}

# Dot-sourcing exposes the orchestration to the offline tests without authenticating.
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    if (-not $CheckOnly -and (-not $ReleaseTag -or -not $AssetSha256)) {
        throw 'Supply -ReleaseTag and -AssetSha256 for an update, or use -CheckOnly.'
    }
    if (-not $CheckOnly) {
        $parsedVersion = $null
        if (-not [version]::TryParse($ReleaseTag.Substring(5), [ref]$parsedVersion)) {
            throw 'The release tag version cannot be represented as a Windows product version.'
        }
    }
    $script:StoreRequestHeaders = Get-StoreRequestHeaders
    Invoke-StoreUpdate $ReleaseTag $AssetSha256 -OnlyCheck:$CheckOnly
}
