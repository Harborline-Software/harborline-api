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

## SQLite pool canary isolation

The full gate at `1c1cfd8b` measured **4,294 total / 4,272 passed / 1 failed / 21 skipped**. The sole failure was `SqliteTestDatabaseTests.Pooled_context_holds_the_file_after_the_context_is_disposed`: deleting the database unexpectedly succeeded. All replay and activation-lifetime cases passed. The wrapper terminated with no receipt; no baseline or allowance changed. Retained host TRX: `artifacts/t433-action-contract/first-use-host-1c1cfd8b.trx`, SHA-256 `c544d57ff7b9f25ff58b6845248225bb9b60575c04b13a7ff0b36c33e1627b7c`. Method-multiplicity reconciliation proves +130 executed rows across 68 methods versus central main, zero removed methods.

The unchanged canary passed three isolated runs (1/1 each). A deterministic temporary probe inserted `SqliteConnection.ClearAllPools()` between disposing the context and the original `Assert.Throws<IOException>`; it reproduced the exact failure (0 passed / 1 failed). This process-global operation occurs in several parallel test classes, including the calendar, encryption and payment repository fixtures. The probe TRX is `apps/local-node-host/tests/TestResults/sqlite-pooled-interference-red.trx`, SHA-256 `94acaf45135bae0ac247b2247caf32619e59a2247a51a657f5ebf923de5e81fe`; its log and the three isolated logs are retained under `artifacts/t433-action-contract`. The experiment was removed, not shipped.

The canary class now joins the existing `Harborline process environment` collection. Its definition sets `DisableParallelization = true`; the installed xUnit 2.9.3 contract explicitly excludes parallel execution with other collections. The Windows-only scope, file-lock assertion, pooling configuration and nonpooled fixture-helper assertion are unchanged. This isolates the global-state observation without a retry, waiver or weaker expectation. A combined run with the canary and real pool-clearing encryption/calendar/payment/outbox classes passed **40/40**, zero skips/failures. Green log: `artifacts/t433-action-contract/sqlite-pool-isolation-green.log`; TRX SHA-256 `48aaa987c11d4b3a6c5e88ce4606f164340646dee1816696e1d564e79c24962c`. A fresh full gate is still required.

## Measured baseline and final analyzer checks

The complete host run at `bbfcc15b` passed **4,294 / 4,273 / 0 / 21**; its only wrapper failure was the previous count inventory. [The measured inventory](t433-producer-host-inventory.md) records all 68 added methods and 130 executed rows. Commit `1b19a6dd` updates only measured Windows count/provenance, with every failure, skip and retry policy unchanged. The next full run confirmed **4,294 / 4,273 / 0 / 21** again and passed every exact-clone check, including host-baseline-match, but stopped at quality-baseline on two new findings. No receipt was issued. Log: `artifacts/t433-action-contract/full-gate-measured-1b19a6dd.log`; retained host TRX SHA-256 `7c261a517103a81d7029399139fe85de249b4ce0f5ad6d4a2fd10fa1b3a58643`.

CA1865 identified a single-character string prefix check in `ViewRequestDescriptor`; it now uses the character overload with explicit ordinal comparison. The first focused build caught CA1307 when that comparison argument was omitted; `quality-sites-green.log` preserves it. CA1869 identified per-call Web JSON options in `RenderPlanCompiler`; it now reuses `JsonSerializerOptions.Web`. Neither change alters the admitted protocol or serialization options. The focused admission/compiler/render-plan run passed **49/49** (`quality-sites-green-v2.log`, TRX SHA-256 `101e1dea0d54f2de4aa088d9fa2b6d8a41715abfd31db8a9b75524dd2dd88afb`). Both owning projects were then rebuilt in Release with quality analysis enabled; separate SARIF reports under `artifacts/t433-action-contract/quality-sites-sarif` contain no CA1865, CA1869 or CA1307 at either changed site. The quality baseline is untouched; another full gate must issue the receipt.

## Projection-authority test interference

The full gate at `a547da55` measured **4,294 / 4,272 / 1 / 21** and issued no receipt. `AuthorizationWriteStageTests.PackProjectionAuthority_IsConsumedOnceByProjector` failed because the second use unexpectedly did not throw. Retained TRX: `artifacts/t433-action-contract/analyzers-host-a547da55.trx`, SHA-256 `d28ecc03b42336733dda737cf00914410622a5d6d2ab38b343400581d2c4aa8b`. Neither the projector nor the one-shot test differs from central main. The failing interval (06:02:46.121–54.051 local) overlaps `A_Narrowed_Binding_Survives_Upgrade_And_Replay` (06:02:48.307–56.413), whose test helper reflectively clears the process-wide consumed-authority dictionary.

The original one-shot test passed three isolated runs. A temporary probe then cleared that dictionary between its first and second projection calls and reproduced the exact missing-exception failure (0/1), TRX SHA-256 `b86bffc224fcfc2ac3839e5db23d296e493ff014ab44c142859ffaa3740357b3`. The probe was removed and the one-shot assertion is unchanged.

Three obsolete `SimulateProcessRestart` helpers and their nine calls are removed from `PackAccessContentTests`, `PackNarrowingSurvivesReplayTests` and `PackReplacementRemovalTests`. Their seven direct replay calls already mint fresh authority through `PackSeedProjectorTestExtensions`; their two reconciliation calls use the production `FromAdmission` path, which also mints a fresh nonce. No path reuses the prior authority object, so erasing every other test's consumed nonces was unnecessary. Real projection/reconciliation calls and all state/retirement assertions remain; no production replay guard, test identity or parallelization policy changes. An initial cleanup build caught one remaining helper call and is preserved as `authority-clear-removal-green-1.log`. The corrected four-class suite passed **68/68 in three consecutive runs** (`authority-clear-removal-green-v2..v4.log`); final TRX SHA-256 `06fe9e5228ec9c98b1c5aa6b9cdfa60f11658933c6f3730012cce361bdf2a9ef`. Host and quality baselines remain unchanged pending the next full gate.
