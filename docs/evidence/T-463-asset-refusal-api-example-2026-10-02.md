# T-463 asset refusals and API example: bounded follow-up

Base: API PR #332 head `29f036ca0f1fe55804b05925e843c0c388b0b333`, still open/ready at refresh.
API origin/main `bf60fed771208e64c0155ecfedca1bbed022e2b2`; Control origin/main
`e1618e95ffd8f4afb40afac102ad131f6ed59a80`. Isolated worktree/branch:
`api-t463-followup`, `work/t463-asset-refusal-api-example`. No source-engine, pipeline,
package-pin, authorization configuration or Kernel-owned file changes.

## Required passing regression checks

`eng/test-t463-fixture-admission.ps1` now performs 13 admission probes (previously 11), including
asset and ledger cases referencing a nonexistent fixture. Both refuse with the independent literal
`verification-fixture-unknown`; no admitted suite is produced. All 13 passed against the cached
production parser, SHA-256 `91ef05290375ee3c90513624befeea56566d19baa1cd1d6c51474845e54c6034`, at
`C:/Projects/Harborline/harborline-platform/projections/dotnet/blocks/hlp.blocks.builder-definitions/bin/Debug/net10.0`.
This binary's source/pin provenance is not established. These are production admission observations,
not exact-head host execution. The unknown `ledger.post` action remains a required passing refusal.

PowerShell AST parsing of the API example passed. Contract generation `--check` passed;
the missing-PackageKey preflight refused before any network call.
`node tooling/harborline-contract-codegen/run-tests.mjs` passed 32/32, zero skips.
`git diff --check` passed. No heavy .NET build/test/mutation run started while the other session owns
the machine. No test waiver, blanket skip, permanent-red test or alternative engine added.

## New host tests: execution pending

Three tests added to `VerificationRunnerTests.cs`, using production boundaries in the existing fixture:

| Test | Independent oracle | Boundary and scope |
| --- | --- | --- |
| `An_asset_suite_with_an_unresolved_fixture_refuses_without_a_receipt_or_effective_change` | HTTP 422, `refused`, `verification-fixture-unknown`, absent receipt, effective digest unchanged | Real verify route/platform suite parser; suite fixture reference only |
| `An_asset_field_type_change_is_incompatible_with_the_existing_numeric_examples` | Literal numeric quantity 2 must fail a changed text field schema | Production Form content parser, schema synthesis and schema registry; submission incompatibility only |
| `An_asset_candidate_that_widens_approval_authority_fails_only_the_authority_claim` | Independently authored suite denies clerk approval; literal totals 200/1000/1200 remain passing | Production verifier/rule/authorization interpreters; deliberately defective candidate emits a Failed receipt, effective digest unchanged |

The last case is expected-red business evidence inside a required passing regression test: it asserts
that the real verifier detects the defect. It has not been executed on this follow-up head yet.
The route fixture's outer package gate permits requests; this is not author authorization proof.
Effective digest preservation does not certify durable asset/journal state or recovery.
The prior head's hosted results do not cover these three new tests. Leave the follow-up draft pending
parent review and exact-head hosted validation; do not mark ready or merge it on this evidence.

## Runnable API example and missing capabilities

`eng/examples/t463-configuration.ps1` uses existing endpoints, whole Form documents and server DTOs.
Its two explicit modes are documented in `docs/examples/T-463-api-first-configuration.md`.
No live HTTP node invocation was performed: selected-session credentials/test-node setup are caller
inputs, and no unauthorized production mutation is needed to review this example.

Proposal/save and installed-candidate prepare/verify are separate real paths today. `RecordCheck`
only stores the caller's receipt ID and current working digest; it does not resolve a real completed
receipt. Prepare reads installed Active pack selections, not proposal/Saved-version edit bytes.
T-463 owns this missing verification-to-saved-candidate binding; T-461/T-667 cover transport/release
and installation. The example deliberately does not submit a synthetic check or release anything.
Server detail supplies edited identities, not a semantic field diff. No full lifecycle certification.

Fresh Control T-615 is PARTIAL/NeedsFix: typed Records identity/binding slices exist, while production
authoring, cross-package references and other intent checks remain owed. T-549 is ready/NeedsFix,
blocked by T-619/T-487/T-486/T-493. Invalid authored Record references, immutable-version refinement
admission, unauthorized author changes, durable asset writes and complete governed release acceptance
therefore remain unsupported/unproved by this Form fixture follow-up. Existing tickets retain them;
no replacement persistence/posting engine, new DSL or ticket created. Owner records:
[T-463](https://github.com/Harborline-Software/harborline-control/blob/main/tickets/T-463-run-domain-verification-against-a-candidate-generation/ticket.md),
[T-615](https://github.com/Harborline-Software/harborline-control/blob/main/tickets/T-615-records-definition-identity-and-intent-validation/ticket.md),
[T-549](https://github.com/Harborline-Software/harborline-control/blob/main/tickets/T-549-replace-the-asset-registry-family-for-the-m10-domain-pack/ticket.md).

## Independent-review permission fix

Review found that Proposal mode's unconditional effective reads required `packages:operate`, even
though start/edit/save/read intentionally require only `packages:author`. The example now takes
`baselineDigest` from the start response and reads `/configuration/proposals/{id}` to check proposal
identity, baseline identity and effective preservation. VerifyInstalled still requires operate plus
author, explicitly documented. No permissions, endpoints or production code changed.

`./eng/test-t463-api-example.ps1` runs the actual example with an intercepted transport and checks
literal request order, URL escaping, identity fields, unchanged whole-document body, content kind,
rationale and session forwarding. Before the fix it failed on the first operate-only GET with the
fixture's HTTP 403. After the fix all four scenarios passed: author-only sequence, denied author
stopping after one request, moved effective generation refusal, and proposal identity mismatch refusal.
This is client routing/transport evidence, not a mock engine or production authorization proof.
The three new C# host tests and live HTTP node invocation remain UNRUN.

## Prepared isolated HTTP integration (UNRUN)

Merged the PR332 native-loader fix normally; all three loader scenarios, 13 admission probes and four
client-routing scenarios passed again. No force push or production C# changes.

Added `ConfigurationProposalRouteTests.The_api_example_runs_proposal_save_and_read_against_the_isolated_http_host`:
invokes the actual copied PowerShell client against the existing ephemeral Kestrel/temporary SQLCipher
fixture, then checks one Saved version, unchanged effective identity, zero release/effective/projection
rows. The fixture's outer package gate permits requests; this is real HTTP workflow proof when run,
not production author-only authorization proof. It covers Proposal mode only; VerifyInstalled live
script invocation remains owed. The child has a 30-second timeout and no operational instance is used.
This new integration test is UNRUN pending parent capacity release, as are the three asset tests.
