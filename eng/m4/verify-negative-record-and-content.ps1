[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RuntimeStatePath,

    [ValidateNotNullOrEmpty()]
    [string] $EvidenceLabel = 'SMOKE',

    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExpectedRecordCode = 'entity.validation.body_invalid'
$ExpectedRecordPointer = '/title'
$ExpectedPackCode = 'pack.install.refused.unsupported_content_kind.standards_catalog'
$ExpectedPackPointer = '/contents/0/contentBase64'
$NoteType = 'notes.entry'
$PackKey = 'm4.negative.unsupported-standards'
$PackVersion = '1.0.0'

function Assert-Exact {
    param(
        [Parameter(Mandatory = $true)] $Actual,
        [Parameter(Mandatory = $true)] $Expected,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    if ($Actual -cne $Expected) {
        throw "$Message Expected '$Expected'; received '$Actual'."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)] [bool] $Condition,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)] [string] $Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    try {
        return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-IdentityHash {
    param([AllowEmptyCollection()] [string[]] $Identity)

    return Get-Sha256Text (($Identity | Sort-Object -CaseSensitive) -join "`n")
}

function ConvertFrom-RequiredJson {
    param(
        [Parameter(Mandatory = $true)] [string] $Text,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    try {
        return $Text | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "$Context did not return valid JSON."
    }
}

function Add-CommandEvidence {
    param(
        [Parameter(Mandatory = $true)] [string] $Method,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [int] $Status,
        [Parameter(Mandatory = $true)] [string] $Purpose,
        [string] $Body = $null
    )

    $script:CommandEvidence.Add([ordered]@{
        method = $Method
        path = $Path
        status = $Status
        purpose = $Purpose
        authorization = 'Bearer <DPAPI DesktopToken>'
        body = $Body
    })
}

function Invoke-ApiRequest {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet('GET', 'POST')] [string] $Method,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Purpose,
        [AllowNull()] $JsonBody = $null,
        [AllowNull()] [byte[]] $BinaryBody = $null,
        [string] $EvidenceBody = $null
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method),
        [Uri]::new($script:ApiOrigin + $Path))
    try {
        if ($null -ne $JsonBody) {
            $json = $JsonBody | ConvertTo-Json -Depth 100 -Compress
            $request.Content = [System.Net.Http.StringContent]::new(
                $json,
                [System.Text.Encoding]::UTF8,
                'application/json')
        }
        elseif ($null -ne $BinaryBody) {
            $request.Content = [System.Net.Http.ByteArrayContent]::new($BinaryBody)
            $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        }

        $response = $script:HttpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $text = [System.Text.Encoding]::UTF8.GetString($bytes)
            $status = [int] $response.StatusCode
            Add-CommandEvidence -Method $Method -Path $Path -Status $status -Purpose $Purpose -Body $EvidenceBody
            return [pscustomobject]@{
                Status = $status
                Bytes = $bytes
                Text = $text
                ContentType = $response.Content.Headers.ContentType.MediaType
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Get-NoteSnapshot {
    $path = "/api/local-node/asset-registry/entities?type=$([Uri]::EscapeDataString($NoteType))"
    $response = Invoke-ApiRequest -Method GET -Path $path -Purpose 'Snapshot pack-bound note rows.'
    Assert-Exact $response.Status 200 'The note list request failed.'
    $body = ConvertFrom-RequiredJson $response.Text 'The note list'
    Assert-True ($null -ne $body.entities) 'The note list response omitted entities.'
    $rows = @($body.entities)
    $ids = @($rows | ForEach-Object { [string] $_.id })
    Assert-True (@($ids | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0) 'A note list row omitted its id.'
    return [ordered]@{
        count = $rows.Count
        identitySha256 = Get-IdentityHash $ids
    }
}

function Get-InstalledSnapshot {
    $response = Invoke-ApiRequest -Method GET -Path '/api/local-node/packs/installed' -Purpose 'Snapshot installed pack versions.'
    Assert-Exact $response.Status 200 'The installed-pack list request failed.'
    $rows = @(ConvertFrom-RequiredJson $response.Text 'The installed-pack list')
    $identity = @($rows | ForEach-Object { '{0}@{1}:{2}' -f $_.packKey, $_.version, $_.lifecycle })
    Assert-True (@($identity | Where-Object { $_ -match '^@|^\s*$' }).Count -eq 0) 'An installed-pack row omitted its identity.'
    $candidate = @($rows | Where-Object { $_.packKey -ceq $PackKey -and $_.version -ceq $PackVersion })
    return [ordered]@{
        count = $rows.Count
        identitySha256 = Get-IdentityHash $identity
        candidatePresent = $candidate.Count -gt 0
    }
}

$resolvedStatePath = (Resolve-Path -LiteralPath $RuntimeStatePath).Path
$state = Import-Clixml -LiteralPath $resolvedStatePath
Assert-True ($null -ne $state.DesktopToken) 'The runtime state omitted DesktopToken.'
Assert-True (-not [string]::IsNullOrWhiteSpace([string] $state.BaseUrl)) 'The runtime state omitted BaseUrl.'

$apiUri = [Uri] $state.BaseUrl
Assert-True ($apiUri.Scheme -eq 'http' -and $apiUri.IsLoopback) 'This helper only targets a loopback HTTP node.'
$script:ApiOrigin = $apiUri.AbsoluteUri.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Assert-True (-not [string]::IsNullOrWhiteSpace([string] $state.RunDirectory)) 'Supply OutputDirectory when runtime state has no RunDirectory.'
    $OutputDirectory = Join-Path ([string] $state.RunDirectory) 'evidence-negative'
}
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$resolvedOutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$credential = [System.Management.Automation.PSCredential]::new('desktop', $state.DesktopToken)
$plainToken = $credential.GetNetworkCredential().Password
Assert-True (-not [string]::IsNullOrWhiteSpace($plainToken)) 'DesktopToken could not be unprotected for this user.'

$script:CommandEvidence = [System.Collections.Generic.List[object]]::new()
$script:HttpClient = [System.Net.Http.HttpClient]::new()
$script:HttpClient.Timeout = [TimeSpan]::FromSeconds(30)
$script:HttpClient.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $plainToken)

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$safeLabel = $EvidenceLabel -replace '[^A-Za-z0-9_.-]', '-'
$stem = "$safeLabel-negative-record-and-content-$timestamp"
$artifactPath = Join-Path $resolvedOutputDirectory "$stem.pack"
$evidencePath = Join-Path $resolvedOutputDirectory "$stem.evidence.json"
$commandsPath = Join-Path $resolvedOutputDirectory "$stem.commands.txt"
$hashesPath = Join-Path $resolvedOutputDirectory "$stem.sha256.json"

try {
    # Check 1: the bound notes form requires /title. The rejected write must not add or replace a row.
    $notesBefore = Get-NoteSnapshot
    $invalidRequest = [ordered]@{
        type = $NoteType
        displayName = 'M4 negative control - missing required title'
        values = [ordered]@{}
    }
    $invalid = Invoke-ApiRequest -Method POST -Path '/api/local-node/asset-registry/entities' `
        -Purpose 'Submit an invalid pack-bound note with an empty values object.' `
        -JsonBody $invalidRequest `
        -EvidenceBody '{"type":"notes.entry","displayName":"M4 negative control - missing required title","values":{}}'
    Assert-Exact $invalid.Status 422 'The invalid bound note was not refused with HTTP 422.'
    $invalidBody = ConvertFrom-RequiredJson $invalid.Text 'The invalid bound-note refusal'
    Assert-Exact ([string] $invalidBody.code) $ExpectedRecordCode 'The invalid bound-note refusal code changed.'
    $invalidPointers = @($invalidBody.pointers | ForEach-Object { [string] $_ })
    Assert-Exact $invalidPointers.Count 1 'The invalid bound-note refusal must identify exactly one pointer.'
    Assert-Exact $invalidPointers[0] $ExpectedRecordPointer 'The invalid bound-note refusal pointer changed.'
    $notesAfter = Get-NoteSnapshot
    Assert-Exact $notesAfter.count $notesBefore.count 'The invalid bound-note write changed the row count.'
    Assert-Exact $notesAfter.identitySha256 $notesBefore.identitySha256 'The invalid bound-note write changed the row identities.'

    # Check 2: an own-node-signed unsupported content kind verifies, but install refuses atomically.
    $installedBefore = Get-InstalledSnapshot
    Assert-True (-not $installedBefore.candidatePresent) 'The unsupported-content negative-control pack is already installed.'

    $packRequest = [ordered]@{
        key = $PackKey
        version = $PackVersion
        name = 'M4 unsupported StandardsCatalog negative control'
        description = 'Acceptance-only signed content that has no live projection path.'
        scopeTier = 'Horizontal'
        contents = @(
            [ordered]@{
                key = 'm4.negative.standards'
                kind = 'StandardsCatalog'
                version = '1.0.0'
                content = [ordered]@{}
            }
        )
        dependencies = @()
        capabilityRequirements = @()
    }
    $export = Invoke-ApiRequest -Method POST -Path '/api/local-node/packs/export' `
        -Purpose 'Export and own-node-sign one StandardsCatalog pack.' `
        -JsonBody $packRequest `
        -EvidenceBody '{"key":"m4.negative.unsupported-standards","version":"1.0.0","contents":[{"key":"m4.negative.standards","kind":"StandardsCatalog","version":"1.0.0","content":{}}]}'
    Assert-Exact $export.Status 200 'The unsupported-content pack export failed.'
    Assert-True ($export.Bytes.Length -gt 0) 'The unsupported-content export returned no bytes.'
    Assert-Exact $export.ContentType 'application/octet-stream' 'The unsupported-content export returned the wrong content type.'
    [System.IO.File]::WriteAllBytes($artifactPath, $export.Bytes)
    $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $verify = Invoke-ApiRequest -Method POST -Path '/api/local-node/packs/verify' `
        -Purpose 'Verify the exact signed export bytes.' -BinaryBody $export.Bytes -EvidenceBody '<signed pack bytes>'
    Assert-Exact $verify.Status 200 'The signed unsupported-content pack verification request failed.'
    $verifyBody = ConvertFrom-RequiredJson $verify.Text 'The signed-pack verification'
    Assert-Exact ([string] $verifyBody.verdict) 'Verified' 'The signed unsupported-content pack did not verify.'
    Assert-Exact ([string] $verifyBody.manifestKey) $PackKey 'Verification returned the wrong manifest key.'

    $install = Invoke-ApiRequest -Method POST -Path '/api/local-node/packs/install' `
        -Purpose 'Attempt installation; unsupported StandardsCatalog content must refuse atomically.' `
        -BinaryBody $export.Bytes -EvidenceBody '<same signed pack bytes>'
    Assert-Exact $install.Status 422 'Unsupported StandardsCatalog install did not return HTTP 422.'
    $installBody = ConvertFrom-RequiredJson $install.Text 'The unsupported-content install refusal'
    Assert-True ($installBody.installed -is [bool] -and $installBody.installed -eq $false) 'The unsupported-content response did not prove installed=false.'
    Assert-Exact ([string] $installBody.packKey) $PackKey 'The install refusal named the wrong pack.'
    Assert-Exact ([string] $installBody.version) $PackVersion 'The install refusal named the wrong version.'
    $refusalCodes = @($installBody.refusalCodes | ForEach-Object { [string] $_ })
    Assert-Exact $refusalCodes.Count 1 'The install response must contain exactly one refusal code.'
    Assert-Exact $refusalCodes[0] $ExpectedPackCode 'The unsupported-content refusal code changed.'
    $refusals = @($installBody.refusals)
    Assert-Exact $refusals.Count 1 'The install response must contain exactly one located refusal.'
    Assert-Exact ([string] $refusals[0].code) $ExpectedPackCode 'The located unsupported-content refusal code changed.'
    Assert-Exact ([string] $refusals[0].pointer) $ExpectedPackPointer 'The unsupported-content refusal pointer changed.'
    $previewCodes = @($installBody.preview.refusalCodes | ForEach-Object { [string] $_ })
    $previewRefusals = @($installBody.preview.refusals)
    Assert-Exact $previewCodes.Count 1 'The install preview must contain exactly one refusal code.'
    Assert-Exact $previewCodes[0] $ExpectedPackCode 'The preview unsupported-content refusal code changed.'
    Assert-Exact $previewRefusals.Count 1 'The install preview must contain exactly one located refusal.'
    Assert-Exact ([string] $previewRefusals[0].code) $ExpectedPackCode 'The preview located refusal code changed.'
    Assert-Exact ([string] $previewRefusals[0].pointer) $ExpectedPackPointer 'The preview refusal pointer changed.'

    $installedAfter = Get-InstalledSnapshot
    Assert-True (-not $installedAfter.candidatePresent) 'The refused unsupported-content pack appears in the installed list.'
    Assert-Exact $installedAfter.count $installedBefore.count 'The refused install changed the installed-pack count.'
    Assert-Exact $installedAfter.identitySha256 $installedBefore.identitySha256 'The refused install changed installed-pack identities or lifecycles.'
    $activationRequests = @($script:CommandEvidence | Where-Object { $_.path -like '/api/local-node/packs/activate*' })
    Assert-Exact $activationRequests.Count 0 'The negative helper must never call pack activation.'

    $evidence = [ordered]@{
        schemaVersion = 1
        label = $EvidenceLabel
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        apiOrigin = $script:ApiOrigin
        credentialSource = 'runtime-state.clixml/DesktopToken (DPAPI; value not retained)'
        passed = $true
        invalidBoundRecord = [ordered]@{
            type = $NoteType
            status = $invalid.Status
            code = [string] $invalidBody.code
            pointers = $invalidPointers
            requiredField = 'title'
            detail = [string] $invalidBody.detail
            auditId = if ($null -ne $invalidBody.auditId) { [string] $invalidBody.auditId } else { $null }
            before = $notesBefore
            after = $notesAfter
            recordCountUnchanged = $true
            rowIdentitiesUnchanged = $true
        }
        unsupportedContent = [ordered]@{
            packKey = $PackKey
            version = $PackVersion
            contentKind = 'StandardsCatalog'
            export = [ordered]@{
                status = $export.Status
                byteLength = $export.Bytes.Length
                artifactFile = [System.IO.Path]::GetFileName($artifactPath)
                artifactSha256 = $artifactHash
            }
            verify = [ordered]@{
                status = $verify.Status
                verdict = [string] $verifyBody.verdict
                manifestKey = [string] $verifyBody.manifestKey
                details = @($verifyBody.details)
            }
            install = [ordered]@{
                status = $install.Status
                installed = [bool] $installBody.installed
                action = [string] $installBody.action
                refusalCodes = $refusalCodes
                refusals = @($refusals | ForEach-Object { [ordered]@{ code = [string] $_.code; pointer = [string] $_.pointer } })
                exactOneExpectedRefusal = $true
            }
            before = $installedBefore
            after = $installedAfter
            installedStateUnchanged = $true
            candidateAbsent = $true
            activationAttempted = $false
        }
        commands = @($script:CommandEvidence)
    }

    $commands = @(
        '# Sanitized replay transcript. Authorization values are intentionally absent.',
        "# API_ORIGIN=$script:ApiOrigin",
        "# TOKEN_SOURCE=$resolvedStatePath :: DesktopToken (DPAPI SecureString)",
        ''
    ) + @($script:CommandEvidence | ForEach-Object {
        $bodySuffix = if ([string]::IsNullOrWhiteSpace([string] $_.body)) { '' } else { " BODY=$($_.body)" }
        '{0} {1}{2} -> HTTP {3} # {4}' -f $_.method, $_.path, $bodySuffix, $_.status, $_.purpose
    })

    [System.IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 100), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllLines($commandsPath, $commands, [System.Text.UTF8Encoding]::new($false))

    $hashes = [ordered]@{
        schemaVersion = 1
        label = $EvidenceLabel
        algorithm = 'SHA256'
        files = @(
            foreach ($path in @($PSCommandPath, $artifactPath, $evidencePath, $commandsPath)) {
                [ordered]@{
                    file = [System.IO.Path]::GetFileName($path)
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                    byteLength = (Get-Item -LiteralPath $path).Length
                }
            }
        )
    }
    [System.IO.File]::WriteAllText($hashesPath, ($hashes | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        Passed = $true
        Evidence = $evidencePath
        Commands = $commandsPath
        Artifact = $artifactPath
        Hashes = $hashesPath
        NotesBefore = $notesBefore.count
        NotesAfter = $notesAfter.count
        VerifyVerdict = [string] $verifyBody.verdict
        InstallStatus = $install.Status
        RefusalCode = $refusalCodes[0]
    }
}
finally {
    if ($null -ne $script:HttpClient) {
        $script:HttpClient.DefaultRequestHeaders.Authorization = $null
        $script:HttpClient.Dispose()
    }
    $plainToken = $null
    $credential = $null
    $state = $null
}
