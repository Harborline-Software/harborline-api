# T433 producer integration verification

The producer branch includes API main `46a6e65eeed7f2b21696a88808af8ab916c90ce4`, including central activation atomicity. The selected replacement caller awaits `ActivateAsync` and preserves committed success with post-commit diagnostics. The reserved installer change carries request correlation only; it does not change the transaction or projection algorithm.

## Reconciliation failures and repairs

The quality-enabled full gate at `a18311cd` measured 4,267 host cases: 4,235 passed, 11 failed, 21 skipped. It produced no verification receipt. Its complete log is `artifacts/t433-action-contract/full-gate-reconciled-second.log`; retained host TRX is `reconciled-host-a18311cd.trx`, SHA-256 `ba130dce55c10f6e538f54ede74a642d0126173049924d231dad74b63dd968d1`.

The eleven failures share these reviewed causes:

- Three refusal-catcher fence failures: two new route catchers needed explicit reviewed identities and proof that the caught exception goes only to the shared refusal renderer. New source tests reject planted Message/Decision reads; real route-catch tests compare the five public fields to the renderer, forbid private decision/exception disclosure, and preserve no mutation on replacement denial.
- One entity replay-isolation fixture named an unrelated tenant. Its principal now uses the actual selected team's projected tenant. Both principal identities remain distinct and both requests must independently create records using the same idempotency key.
- Exact inventories: four selected grant routes; eight named CLI exemptions; two selected reads in every profile and six additional web-session routes; the approved `NarrowScopeAsync` store method; one holder-read and four navigation call-site line movements. Gate-before-read and planted-offender proofs remain intact.
- MTW discovery: two identity owners (`AdminGrantActionReplay`, `AdminGrantReviewAuthority`) and five WebSession owners (`AdminGrantActionRoutes`, `KernelAuditMetadataRoutes`, `SelectedFormSubmitRoutes`, `SelectedPackReplacementRoutes`, `SelectedRequestCorrelation`). Regeneration changes only these named additions and their counts.

The first focused repair attempt failed compilation because the two new tests lacked an existing namespace import. The second ran 110 cases, with 109 passing and one new form-test setup failure: it constructed the wrong record kind for `forms:author`. The test now uses the gate's canonical `RecordKindFor(operation)` mapping. Both failed logs remain retained.

After `dotnet clean`, the combined third run passed **110/110**, zero failures/skips, in 1m9s. The filter includes every formerly failing class, both new route-catch tests, planted fence negatives, allow-list vacuity and shared point-of-use tenant guards. MTW regeneration was disabled for this final comparison. Evidence: `artifacts/t433-action-contract/reconcile-inventory-repairs-v3.log` and `apps/local-node-host/tests/TestResults/reconcile-inventory-repairs-v3.trx`.

## Host inventory boundary

Comparing the retained producer TRX to central atomicity's 4,164-case TRX by fully qualified method **and executed-row multiplicity** yields 103 additions across 56 methods and zero removed methods. Distinct display strings alone yield 101 because two long theory displays are truncated; they are not missing executions. The six new refusal-rendering proof cases require a fresh complete measurement before updating the Windows tuple. Failure, skip, retry allowances and other operating-system baselines are unchanged.

Signed fixture bytes are unchanged by reconciliation:

- Final 1.1.2: `392f2545710d5cb3a7df8724e43d65e751607debfafe00898d704819d55a381f`.
- Atomicity probe: `a74360ca88b8abc77433211d1731076540987b3e69afef87aa4cbe21c4f12c13`.

Focused evidence is not a full-gate or release receipt. The committed producer must pass the quality-enabled complete gate before release.

## Independent review: omitted-correlation replay

Review of `046cc809` found that `ValidateReplayContextAsync` returned immediately when the new request omitted correlation. Because the form instance derives from tenant/form/idempotency key, this skipped the original actor and payload comparison as well. `ProjectingFormEngine` then received the old receipt with the new actor/candidate.

Ten new route/projection cases reproduced the issue before the engine change: **7 failed / 3 passed**. Six unsafe selected-session combinations (omitted correlation after correlated submission, changed actor, changed payload, or combinations thereof) and a desktop changed-payload retry returned Created and reached the projection runner twice. Safe identical no-header retries on both routes and refusal when adding correlation to an uncorrelated original already passed. Red evidence: `artifacts/t433-action-contract/replay-context-red.log`; TRX SHA-256 `8caa5487082ff1aaa8c727e77ed193ae3792edcd60bace24cb46295c382e4219`.

The shared engine now always locates the original Mint row and compares actor, request fingerprint and nullable correlation equivalence. CLR null and persisted JSON null mean absent correlation; a missing original Mint still fails closed. Correlation is not globally mandatory. Desktop `FormsRoutes` translates the existing replay-context exception to the same `409 forms.replay_context_mismatch` used by the selected route. The old changed-payload Created expectation is corrected; independent identical-payload/no-header controls retain safe replay.

The focused route/engine/projection suite passed **58/58**, including all ten new cases and the real deterministic Access grant/workflow replay. Conflicts assert zero further projection calls and an unchanged persisted entity version/body/binding and Mint receipt. Green evidence: `artifacts/t433-action-contract/replay-context-green.log`; TRX SHA-256 `8a42607e74fe96e3b34a8c7998b5348e6ecb3df3b2a4fbe6ed71e5486be30fd7`.

## Preserved activation-lifetime failures

The complete gate at `046cc809` measured **4,273 total / 4,250 passed / 2 failed / 21 skipped**. Both failures were `PackProjectionResourceLifetimeTests.Paused_form_enumerator_does_not_block_activation_and_retains_its_snapshot` (false/true), canceling at the process-global activation barrier's write acquisition under the unchanged two-second deadline. No baseline or allowance was changed. Retained TRX: `artifacts/t433-action-contract/repaired-host-046cc809.trx`, SHA-256 `a854d69745aace4145845db88975ec50871b6ac00eee0b2748eb0119961bd431`.

The exact theory passed three isolated runs (2/2 each), then three combined producer/preload/form-definition runs (126/126 each). Logs/TRXs use `resource-lifetime-isolated-1..3` and `resource-lifetime-combined-1..3`. The form store, barrier and lifetime test are unchanged from central main. These reruns do not erase the full-suite failures or establish their cause; another complete run must resolve them. The ten replay cases imply a candidate 4,283-case inventory, subject to fresh measured identity reconciliation, not arithmetic-only baseline approval.

The subsequent complete host run at `6fb07e2f` measured **4,283 total / 4,262 passed / 0 failed / 21 skipped**, including both lifetime variants. TRX `artifacts/t433-action-contract/replay-host-6fb07e2f.trx` has SHA-256 `6e19beba9cac76f90b72c29e16c6d66909a08214b79a0e6884bc61eda6cbf8ee`. Executed-method multiplicity comparison to central main proves +119 rows across 63 methods, zero removed methods. The wrapper completed with only the unchanged baseline-count mismatch and no receipt. The baseline was deliberately not updated because the next review finding remained open.

## Independent review: concurrent first-use adoption

Two equal-body submissions can both observe no prior entity. The store's default equal-body idempotence then returns the same ID to both creates without throwing, bypassing the engine's collision validation and allowing a second Mint/projection. A deterministic test pauses both requests immediately before delegated `IAuthorizedFormEntityWriter.CreateAsync`, completes the winner through Mint/projection, then releases the loser. No sleeps or probabilistic contention are used.

Before the fix, all four race cases failed: changed actor, changed correlation, both, and matching context each produced **two Mints and two projections**. The cancellation control passed. Red evidence: `artifacts/t433-action-contract/concurrent-replay-red.log`; TRX SHA-256 `82df0a1c43379109b0d207413dbe58a978c56b81d48f66ea4285a51cedb4e97a`.

`CreateOptions.RequireNew` defaults to false, preserving existing callers' equal-body adoption. When true, both single and batch insertion check under their existing entity locks and throw the existing `IdempotencyConflictException` for every existing ID. A refused batch rolls back only its newly inserted prefix. The form engine requests this behavior only for idempotency-key-derived IDs, so every lost creation takes winner-read and authenticated replay-context validation before any Mint/projection. No new lock or authorization policy is introduced. The SQLite test adapter honors the option too; this does not add a production backend.

The final focused suite passed **95/95**, zero skips/failures, in 9s. Mismatched concurrent contexts return Conflict with one Mint/one projection; matching context returns Created with one Mint and two idempotent projection invocations. Single/batch default-adoption, strict refusal, rollback and cancellation tests pass. The canceled route probe waits for server-side exit and proves no entity, Mint or projection exists before retry successfully claims the key. Evidence: `artifacts/t433-action-contract/concurrent-replay-final.log`; TRX SHA-256 `bfc5c529511333da8a68bce92d0979ebb12f50c161b7ec98137b4a680db38124`.

These tests add eleven executed cases (four race rows, one route cancellation case, six entity-store rows). A new complete run must measure the candidate 4,294-case inventory before any baseline change or receipt.
