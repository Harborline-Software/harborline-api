# T-463 whole-document API example

Run `eng/examples/t463-configuration.ps1` with PowerShell 7 against a disposable test node,
with an existing selected session and its real authorization headers supplied in memory.
The script does not create an actor, grant permissions, bootstrap a node or install packs.
Do not use the test-only desktop header from the host tests on a deployed node.

```powershell
$asset = 'apps/local-node-host/tests/Configuration/Fixtures/T463/asset.candidate.json'
./eng/examples/t463-configuration.ps1 -BaseUri $testNode -Headers $sessionHeaders `
  -Mode Proposal -ProposalId 'asset-example-1' -PackageKey $owningPackage `
  -CandidateFile $asset -Rationale 'Evaluate the asset intake form as a whole document.'
```

Proposal mode requires `packages:author` only. It calls POST `/api/local-node/configuration/proposals`,
PUT `/configuration/proposals/{proposalId}/edits`, POST `/configuration/proposals/{proposalId}/versions`,
then GET `/configuration/proposals/{proposalId}` (all shortened paths retain `/api/local-node`).
The start response supplies `baselineDigest`; the final author-scoped read checks the proposal and
baseline identities and compares its `effectiveDigest` with that baseline. It does not call the
operate-only `/configuration/effective` route. `bodyJson` is the unmodified full document;
`contentKind` is `FormDefinition`. Output contains the server's edited-definition summary,
working digest, immutable Saved version and detail bindings. This summary is not a field-level diff.
JSON syntax and transport admission do not prove Records intent, references or refinement validity.
The example checks that the effective digest stays unchanged; proposal and Saved version rows are
intentional writes. A failure leaves those draft rows available for inspection; it does not roll them back.

For a candidate already installed as Active packs on the test node:

```powershell
./eng/examples/t463-configuration.ps1 -BaseUri $testNode -Headers $sessionHeaders `
  -Mode VerifyInstalled -PackageKey $owningPackage -ActivePackageKeys $installedPackKeys `
  -SuiteFile 'apps/local-node-host/tests/Configuration/Fixtures/T463/asset.suite.json' `
  -ReceiptId 'asset-example-check-1'
```

VerifyInstalled requires both `packages:operate` (effective reads/prepare) and `packages:author` (verify).
This calls GET `/configuration/effective`, POST `/configuration/prepare`, POST `/configuration/verify`,
then GET `/configuration/effective`. Prepare uses explicitly selected installed Active packages and
ownership, not the candidate file or Saved version. It may store an isolated prepared projection.
Verify requires a `Passed` receipt for the exact baseline/candidate digests; HTTP 200 with `Failed`
or `Unsupported` is an error. HTTP refusals retain server codes, targets and messages. A real node
must have compatible installed definitions and roles; the script does not install the test fixture's
`finance.access` helper. Asset initial facts and declared ports are suite inputs, not proof of durable
asset writes. The numeric totals are examples, not a money primitive.

## Why the example stops before release

`ConfigurationProposalStore.RecordCheck` accepts a receipt ID and binds it to the current working
digest without looking up a completed verification receipt. `/configuration/prepare` accepts installed
package selections, not proposal edits or Saved versions. Consequently this example cannot establish
that verification ran against the saved proposal bytes. Calling `/checks` with an invented ID and then
`/release` would hide the gap. T-463 owns that receipt/candidate binding; T-461/T-667 own the existing
proposal/release/install transport. Release/install/activate remain available production operations,
but this example does not invoke them or certify the complete governed lifecycle.

T-615 owns Records cross-package reference and definition-version/refinement admission. The new
`verification-fixture-unknown` probe checks a suite fixture reference only. The incompatible quantity
schema test checks existing submissions against a changed Form schema; it is not a definition-change
migration gate. T-549 owns the authored asset replacement/persistence path. The widened approval
policy test detects an unauthorized result using the runner's real rule/authorization interpreters;
it does not prove that an unauthorized author cannot submit a proposal (route tests here use an
allowing package gate). No Kernel-owned engine or source is changed.
