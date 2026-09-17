#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeStatePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$EvidenceLabel = 'M4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$state = Import-Clixml -LiteralPath $RuntimeStatePath
$baseUri = [uri]($state.BaseUrl.TrimEnd('/') + '/')
$nodeProcess = Get-Process -Id ([int]$state.ProcessId) -ErrorAction Stop
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'OutputDirectory already exists.' }
$null = New-Item -ItemType Directory -Path $outputPath
$evidencePath = Join-Path $outputPath "$EvidenceLabel-authenticated-record-denial.evidence.json"

function New-SessionState { @{ Cookies = @{}; Antiforgery = $null } }

function Assert-Node {
    $nodeProcess.Refresh()
    if ($nodeProcess.HasExited) { throw 'The acceptance node exited.' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort ([int]$state.Port) -ErrorAction SilentlyContinue)
    if (!$listeners.Count -or @($listeners | Where-Object OwningProcess -ne $nodeProcess.Id).Count) {
        throw 'The configured port is not owned exclusively by the acceptance node.'
    }
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$handler.UseProxy = $false
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-Node {
    param(
        [Parameter(Mandatory)][hashtable]$Session,
        [Parameter(Mandatory)][string]$Path,
        [string]$Method = 'GET',
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )
    if (!$Path.StartsWith('/') -or $Path.StartsWith('//')) { throw 'Only fixed node-relative paths are allowed.' }
    Assert-Node
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), [uri]::new($baseUri, $Path))
    try {
        if ($Session.Cookies.Count) {
            $cookieHeader = ($Session.Cookies.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join '; '
            $null = $request.Headers.TryAddWithoutValidation('Cookie', $cookieHeader)
        }
        if ($Session.Antiforgery) {
            $null = $request.Headers.TryAddWithoutValidation('X-Harborline-Antiforgery', $Session.Antiforgery)
        }
        foreach ($header in $Headers.GetEnumerator()) {
            $null = $request.Headers.TryAddWithoutValidation([string]$header.Key, [string]$header.Value)
        }
        if ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(
                ($Body | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            if ($response.Headers.Contains('Set-Cookie')) {
                foreach ($cookie in $response.Headers.GetValues('Set-Cookie')) {
                    $pair = (($cookie -split ';', 2)[0] -split '=', 2)
                    if ($pair.Count -ne 2) { throw 'Malformed session cookie.' }
                    if ($pair[1]) { $Session.Cookies[$pair[0]] = $pair[1] } else { $Session.Cookies.Remove($pair[0]) }
                }
            }
            if ($response.Headers.Contains('X-Harborline-Antiforgery')) {
                $Session.Antiforgery = @($response.Headers.GetValues('X-Harborline-Antiforgery'))[0]
            }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $bodyValue = if ([string]::IsNullOrWhiteSpace($text)) { $null } else { $text | ConvertFrom-Json }
            [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $bodyValue }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Assert-Status($Response, [int]$Expected, [string]$Stage) {
    if ($Response.Status -ne $Expected) { throw "$Stage returned HTTP $($Response.Status), expected $Expected." }
}

function Open-SelectedSession([string]$Username, [string]$Password) {
    $session = New-SessionState
    Assert-Status (Invoke-Node $session '/api/session/antiforgery') 204 'antiforgery handshake'
    Assert-Status (Invoke-Node $session '/api/session/account-challenge' 'POST' @{ username=$Username; password=$Password }) 200 'account challenge'
    Assert-Status (Invoke-Node $session '/api/session/select' 'POST' @{ tenantId=$null }) 200 'tenant selection'
    $who = Invoke-Node $session '/api/session/whoami'
    Assert-Status $who 200 'selected whoami'
    [pscustomobject]@{ Session=$session; Who=$who.Body }
}

$founderPassword = [Net.NetworkCredential]::new('', $state.Founder.Password).Password
$joinerPassword = 'M4-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + '!a'
$joinerUsername = 'm4-denied-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$founder = $null
$joiner = $null
try {
    $founder = Open-SelectedSession $state.Founder.UserName $founderPassword
    if (@($founder.Who.permissions) -notcontains 'members:manage') { throw 'Founder selected session lacks members:manage.' }
    $tenantId = [string]$founder.Who.tenant.id

    $invite = Invoke-Node $founder.Session '/api/session/admin/invitations' 'POST' @{
        requestedPermissions = @('records:read','records:write','audit:trace-read')
        idempotencyKey = 'm4-denied-' + [guid]::NewGuid().ToString('N')
    }
    Assert-Status $invite 200 'issue invitation'
    if ([string]::IsNullOrWhiteSpace([string]$invite.Body.code)) { throw 'Invitation response omitted its one-time code.' }

    $accept = New-SessionState
    Assert-Status (Invoke-Node $accept '/api/session/antiforgery') 204 'joiner antiforgery handshake'
    Assert-Status (Invoke-Node $accept '/api/session/account-setup-accept' 'POST' @{
        code=[string]$invite.Body.code; tenantId=$tenantId; username=$joinerUsername; password=$joinerPassword
    }) 200 'accept invitation'
    $invite.Body.code = $null
    $accept.Cookies.Clear(); $accept.Antiforgery = $null

    $joiner = Open-SelectedSession $joinerUsername $joinerPassword
    $before = Invoke-Node $joiner.Session '/api/local-node/asset-registry/entities?type=notes.entry'
    Assert-Status $before 200 'list notes before denial'
    $beforeIds = @($before.Body.entities | ForEach-Object { [string]$_.id } | Sort-Object)

    $joiner.Session.Cookies.Clear(); $joiner.Session.Antiforgery = $null
    $joiner = Open-SelectedSession $joinerUsername $joinerPassword
    if (@($joiner.Who.permissions) -contains 'packages:author' -or @($joiner.Who.permissions) -notcontains 'audit:trace-read') {
        throw 'Fresh member session did not expose the expected non-author closure.'
    }

    $denied = Invoke-Node $joiner.Session '/api/local-node/packs/export?validateOnly=true' 'POST' @{
        key='m4.denied.member-authoring'; name='M4 denied member authoring'; version='1.0.0'; scopeTier='Team'; contents=@()
    }
    Assert-Status $denied 403 'member pack-authoring write'
    if ([string]$denied.Body.code -cne 'web-plane.route.unavailable') {
        throw "The web-plane fence returned unexpected code '$($denied.Body.code)'."
    }

    $after = Invoke-Node $joiner.Session '/api/local-node/asset-registry/entities?type=notes.entry'
    Assert-Status $after 200 'list notes after denial'
    $afterIds = @($after.Body.entities | ForEach-Object { [string]$_.id } | Sort-Object)
    if (($beforeIds -join ',') -cne ($afterIds -join ',')) { throw 'Denied write changed record identities.' }

    $evidence = [ordered]@{
        schemaVersion=1; label=$EvidenceLabel; passed=$true; completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
        node=@{baseUrl=$state.BaseUrl; processId=[int]$state.ProcessId; tenantId=$tenantId}
        founder=@{standing=$founder.Who.standing; route='/api/session/admin/invitations'; authority='members:manage'; antiforgery='consumed'}
        disposableMember=@{partyId=$joiner.Who.partyId; permissions=@($joiner.Who.permissions | Sort-Object); reauthenticated=$true; missingRequiredPermission='packages:author'}
        deniedWrite=@{operation='packages:author'; target='m4.denied.member-authoring'; status=403; code=$denied.Body.code; auditId=$null; refusalLayer='web-plane route fence'; recordIdentitiesUnchanged=$true; beforeCount=$beforeIds.Count; afterCount=$afterIds.Count}
        trace=@{availability='not disclosed by perimeter refusal'; positiveRecordWriteTrace='captured separately in the lane UI transcript'}
    }
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    [pscustomobject]@{Passed=$true; Evidence=$evidencePath; DeniedStatus=403; Code=$denied.Body.code; Before=$beforeIds.Count; After=$afterIds.Count}
}
finally {
    $http.Dispose()
    $founderPassword = $null; $joinerPassword = $null; $joinerUsername = $null
    if ($founder) { $founder.Session.Cookies.Clear(); $founder.Session.Antiforgery = $null }
    if ($joiner) { $joiner.Session.Cookies.Clear(); $joiner.Session.Antiforgery = $null }
}
