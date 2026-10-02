<#
T-463 lightweight fixture admission. Uses the released production parser, never a model engine.
Provide an already built Platform/feed directory. No restore, compilation, persistence or activation.
Future posting expectations are a non-executable manifest, outside these regression checks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssemblyDirectory
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path $PSScriptRoot '../apps/local-node-host/tests/Configuration/Fixtures/T463'
$assemblyRoot = (Resolve-Path -LiteralPath $AssemblyDirectory).Path
Get-ChildItem -LiteralPath $assemblyRoot -Filter '*.dll' | ForEach-Object {
    [System.Reflection.Assembly]::LoadFrom($_.FullName) | Out-Null
}
$producer = Join-Path $assemblyRoot 'Harborline.Blocks.BuilderDefinitions.dll'
Write-Output "Production parser SHA256: $((Get-FileHash -LiteralPath $producer -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Output 'Evidence: admission only; build provenance supplied by caller; no host execution or release certification.'
function Read-Suite([string]$name) {
    Get-Content -LiteralPath (Join-Path $fixtureRoot $name) -Raw | ConvertFrom-Json -AsHashtable
}
function Admit($document) {
    $refusals = $null
    $suite = [Harborline.Blocks.BuilderDefinitions.VerificationSuite]::Parse(
        ($document | ConvertTo-Json -Depth 100 -Compress), [ref]$refusals)
    @{ Suite = $suite; Codes = @($refusals | ForEach-Object Code) }
}
function Require-Refusal($result, [string]$code, [string]$label) {
    if ($null -ne $result.Suite -or $result.Codes -notcontains $code) {
        throw "$label expected $code and no admitted suite; got $($result.Codes -join ',')"
    }
    Write-Output "PASS $label : $code"
}
$oracle = Get-Content -LiteralPath (Join-Path $fixtureRoot 'expectations.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($name in @('asset.suite.json', 'ledger.suite.json')) {
    $result = Admit (Read-Suite $name)
    if ($null -eq $result.Suite -or $result.Codes.Count -ne 0) {
        throw "$name must admit; got $($result.Codes -join ',')"
    }
    Write-Output "PASS $name : admitted $($result.Suite.Digest)"
    # Mutate one authoring input at a time, then exercise the production admission boundary.
    $empty = Read-Suite $name
    $empty.cases[0].assertions = @()
    Require-Refusal (Admit $empty) $oracle.requiredRefusals.emptyAssertions "$name empty assertions"
    $wrong = Read-Suite $name
    $wrong.cases[0].assertions[1].expected = '"true"'
    Require-Refusal (Admit $wrong) $oracle.requiredRefusals.wrongExpectedType "$name wrong boolean type"
    $duplicate = Read-Suite $name
    $duplicate.cases[1].caseId = $duplicate.cases[0].caseId
    Require-Refusal (Admit $duplicate) 'verification-case-duplicate' "$name duplicate stable case identity"
    $invalidFact = Read-Suite $name
    $invalidFact.fixtures[0].facts[0].body = '{'
    Require-Refusal (Admit $invalidFact) 'verification-fixture-fact-invalid' "$name malformed initial record"
    $missingFixture = Read-Suite $name
    $missingFixture.cases[0].fixtureId = 'not-declared'
    Require-Refusal (Admit $missingFixture) 'verification-fixture-unknown' "$name unresolved fixture reference"
}
$unsupported = Admit (Read-Suite 'ledger-post.future.suite.json')
Require-Refusal $unsupported $oracle.requiredRefusals.unknownAction 'ledger posting capability boundary'
Write-Output 'PASS required admission regressions. Asset/ledger persistence, no-mutation observations and governed release acceptance remain NOT IMPLEMENTED in this harness.'
