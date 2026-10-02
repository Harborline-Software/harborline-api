<#
Request-routing regression for the example client, not host/authorization-engine evidence.
Intercepts Invoke-WebRequest only; runs the actual example and checks its wire requests.
#>
$ErrorActionPreference = 'Stop'
$example = Join-Path $PSScriptRoot 'examples/t463-configuration.ps1'
$candidate = Join-Path $PSScriptRoot '../apps/local-node-host/tests/Configuration/Fixtures/T463/asset.candidate.json'
$candidateBody = Get-Content -LiteralPath $candidate -Raw
$requests = [System.Collections.Generic.List[object]]::new()
$scenario = 'author-only'
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Invoke-WebRequest {
    param($Uri, $Method, $Headers, $SkipHttpErrorCheck, $ContentType, $Body)
    $path = ([uri]$Uri).AbsolutePath
    $requests.Add([pscustomobject]@{ Method = $Method; Path = $path; Body = $Body })
    Assert ($Headers.Authorization -eq 'fixture-author-session') 'Session header was not forwarded.'
    Assert $SkipHttpErrorCheck 'Server refusal bodies must be preserved.'
    # This endpoint requires packages:operate in production. An author-only fixture refuses it.
    if ($path -eq '/api/local-node/configuration/effective' -or $scenario -eq 'author-denied') {
        return @{ StatusCode = 403; Content = '{"code":"fixture-permission-denied"}' }
    }
    if ($Method -eq 'POST' -and $path -eq '/api/local-node/configuration/proposals') {
        Assert (($Body | ConvertFrom-Json).proposalId -eq 'asset example') 'Wrong start body.'
        $response = @{ proposalId = 'asset example'; baselineDigest = 'baseline-1'; effectiveDigest = 'baseline-1' }
    } elseif ($Method -eq 'PUT' -and $path -eq '/api/local-node/configuration/proposals/asset%20example/edits') {
        Assert ($ContentType -eq 'application/json') 'Edit must use JSON transport.'
        $edit = $Body | ConvertFrom-Json
        Assert ($edit.definitionKey -eq 'records/asset' -and $edit.packageKey -eq 'example.asset') 'Wrong edit identities.'
        Assert ($edit.contentKind -eq 'FormDefinition' -and $edit.bodyJson -ceq $candidateBody) 'Candidate bytes/kind changed.'
        $response = @{ workingDigest = 'working-1'; edits = @(@{ definitionKey = 'records/asset'; packageKey = 'example.asset'; contentKind = 'FormDefinition' }) }
    } elseif ($Method -eq 'POST' -and $path -eq '/api/local-node/configuration/proposals/asset%20example/versions') {
        Assert (($Body | ConvertFrom-Json).rationale -eq 'Independent example rationale') 'Wrong save body.'
        $response = @{ savedVersion = @{ ordinal = 1; digest = 'saved-1' }; detail = @{ status = 'Saved version' } }
    } elseif ($Method -eq 'GET' -and $path -eq '/api/local-node/configuration/proposals/asset%20example') {
        $response = @{ proposalId = 'asset example'; baselineDigest = 'baseline-1'; effectiveDigest = 'baseline-1' }
        if ($scenario -eq 'effective-moved') { $response.effectiveDigest = 'baseline-2' }
        if ($scenario -eq 'identity-mismatch') { $response.proposalId = 'another-proposal' }
    } else { throw "Unexpected request: $Method $path" }
    @{ StatusCode = 200; Content = ($response | ConvertTo-Json -Depth 20 -Compress) }
}
$arguments = @{
    BaseUri = 'http://example.invalid'; Headers = @{ Authorization = 'fixture-author-session' }
    Mode = 'Proposal'; ProposalId = 'asset example'; PackageKey = 'example.asset'
    CandidateFile = $candidate; Rationale = 'Independent example rationale'
}
$output = & $example @arguments | ConvertFrom-Json
Assert ($output.baselineDigest -eq 'baseline-1' -and $output.effectiveDigest -eq 'baseline-1') 'Wrong returned generation identities.'
Assert (($requests | ForEach-Object { "$($_.Method) $($_.Path)" }) -join "`n" -ceq @'
POST /api/local-node/configuration/proposals
PUT /api/local-node/configuration/proposals/asset%20example/edits
POST /api/local-node/configuration/proposals/asset%20example/versions
GET /api/local-node/configuration/proposals/asset%20example
'@) 'Proposal mode must only call author-scoped routes in the expected sequence.'
Write-Output 'PASS author-only proposal request sequence, bodies and unchanged effective identity'
foreach ($case in @(
    @{ Scenario = 'author-denied'; Error = 'HTTP 403:.*fixture-permission-denied'; Count = 1 },
    @{ Scenario = 'effective-moved'; Error = 'Effective generation changed'; Count = 4 },
    @{ Scenario = 'identity-mismatch'; Error = 'Proposal identity changed'; Count = 4 }
)) {
    $scenario = $case.Scenario
    $requests.Clear()
    $failure = $null
    try { $null = & $example @arguments } catch { $failure = $_.Exception.Message }
    Assert ($failure -match $case.Error) "$scenario did not produce the expected refusal: $failure"
    Assert ($requests.Count -eq $case.Count) "$scenario made unexpected additional requests."
    Write-Output "PASS $scenario refusal and request boundary"
}
