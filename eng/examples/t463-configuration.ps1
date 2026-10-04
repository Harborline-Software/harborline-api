<#
API-first T-463 example. PowerShell 7; a running test node and selected-session authorization
are supplied by the caller. Proposal writes a draft and Saved version. VerifyInstalled prepares
and verifies existing Active packs. Neither mode releases, installs or activates a package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][ValidateSet('Proposal', 'VerifyInstalled')][string]$Mode,
    [hashtable]$Headers = @{},
    [string]$ProposalId,
    [string]$DefinitionKey = 'records/asset',
    [string]$PackageKey,
    [string]$CandidateFile,
    [string]$Rationale,
    [string[]]$ActivePackageKeys,
    [string]$SuiteFile,
    [string]$ReceiptId
)
$ErrorActionPreference = 'Stop'
function Phase([string]$phase) {
    if ($env:T463_EXAMPLE_DIAGNOSTICS -eq '1') { [Console]::Error.WriteLine("configuration-example: $phase") }
}
Phase 'script-started'
if (-not $BaseUri.IsAbsoluteUri -or $BaseUri.Scheme -notin @('http', 'https')) {
    throw 'BaseUri must be an absolute HTTP(S) test-node URL.'
}
function Request([string]$method, [string]$path, $body = $null) {
    $arguments = @{
        Uri = "$($BaseUri.AbsoluteUri.TrimEnd('/'))$path"
        Method = $method
        Headers = $Headers
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $body | ConvertTo-Json -Depth 100 -Compress
    }
    Phase "request-start $method $path"
    $response = Invoke-WebRequest @arguments
    Phase "request-complete $method $path HTTP $($response.StatusCode)"
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        # Preserve server refusal codes/targets/messages. Do not print authorization headers.
        throw "$method $path returned HTTP $($response.StatusCode): $($response.Content)"
    }
    $response.Content | ConvertFrom-Json
}
function Required([string]$value, [string]$name) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw "$name is required for $Mode." }
}
Phase 'inputs-validating'
# Check all inputs before the first write.
Required $PackageKey 'PackageKey'
Required $DefinitionKey 'DefinitionKey'
if ($Mode -eq 'Proposal') {
    Required $ProposalId 'ProposalId'
    Required $CandidateFile 'CandidateFile'
    Required $Rationale 'Rationale'
    $candidate = Get-Content -LiteralPath $CandidateFile -Raw
    $null = $candidate | ConvertFrom-Json
} else {
    Required $SuiteFile 'SuiteFile'
    Required $ReceiptId 'ReceiptId'
    if (-not $ActivePackageKeys -or $ActivePackageKeys -notcontains $PackageKey) {
        throw 'ActivePackageKeys must explicitly include PackageKey.'
    }
    $suite = Get-Content -LiteralPath $SuiteFile -Raw
    $null = $suite | ConvertFrom-Json
}
Phase 'inputs-validated'
if ($Mode -eq 'Proposal') {
    $id = [uri]::EscapeDataString($ProposalId)
    $started = Request 'POST' '/api/local-node/configuration/proposals' @{ proposalId = $ProposalId }
    $baselineDigest = $started.baselineDigest
    Required $baselineDigest 'Server baselineDigest'
    if ($started.proposalId -ne $ProposalId) { throw 'Proposal identity changed in the start response.' }
    if ($started.effectiveDigest -ne $baselineDigest) { throw 'Effective generation changed during proposal start; inspect the draft before continuing.' }
    $edited = Request 'PUT' "/api/local-node/configuration/proposals/$id/edits" @{
        definitionKey = $DefinitionKey; packageKey = $PackageKey
        bodyJson = $candidate; contentKind = 'FormDefinition'
    }
    $saved = Request 'POST' "/api/local-node/configuration/proposals/$id/versions" @{ rationale = $Rationale }
    $result = [ordered]@{
        mode = $Mode; baselineDigest = $baselineDigest
        workingDigest = $edited.workingDigest; edits = $edited.edits
        savedVersion = $saved.savedVersion; detail = $saved.detail
        verification = 'NOT EXECUTED: saved proposal edits are not an input to prepare.'
    }
    $read = Request 'GET' "/api/local-node/configuration/proposals/$id"
    if ($read.proposalId -ne $ProposalId -or $read.baselineDigest -ne $baselineDigest) {
        throw 'Proposal identity changed in the final read.'
    }
    $effectiveDigest = $read.effectiveDigest
} else {
    # These installed-candidate operations require packages:operate as well as packages:author.
    $baseline = Request 'GET' '/api/local-node/configuration/effective'
    $baselineDigest = $baseline.digest
    $prepared = Request 'POST' '/api/local-node/configuration/prepare' @{
        expectedBaselineDigest = $baseline.digest; activePackageKeys = @($ActivePackageKeys)
        ownership = @(@{ definitionKey = $DefinitionKey; packageKey = $PackageKey })
    }
    if ($prepared.status -ne 'preparing' -or -not $prepared.candidateDigest) {
        throw "Prepare did not return a prepared candidate: $($prepared | ConvertTo-Json -Depth 100 -Compress)"
    }
    $verified = Request 'POST' '/api/local-node/configuration/verify' @{
        expectedBaselineDigest = $baseline.digest; candidateDigest = $prepared.candidateDigest
        receiptId = $ReceiptId; suite = $suite
    }
    if ($verified.status -ne 'Passed' -or $verified.candidateDigest -ne $prepared.candidateDigest -or
        $verified.baselineDigest -ne $baseline.digest -or -not $verified.receiptDigest) {
        throw "Verification did not pass for the requested digests: $($verified | ConvertTo-Json -Depth 100 -Compress)"
    }
    $result = [ordered]@{ mode = $Mode; preparation = $prepared; verification = $verified }
    $effective = Request 'GET' '/api/local-node/configuration/effective'
    $effectiveDigest = $effective.digest
}
if ($effectiveDigest -ne $baselineDigest) { throw 'Effective generation changed during the example; inspect concurrent activity.' }
$result.effectiveDigest = $effectiveDigest
Phase 'workflow-complete'
$result | ConvertTo-Json -Depth 100
