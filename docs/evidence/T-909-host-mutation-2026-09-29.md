# T-909 host mutation evidence: compiled-shape refusal (ck-1) and KernelClock consumers (ck-9)

This note records the ruling-95 host Stryker slices for what api PR #251 added. PR #251's squash commit `b5efec7c` is empty: its tree equals its parent's, because the stacked PR #255 (`695b7ce7b0a3a51273e0099de5dc67fadc072cde`) merged first and carries every #251 change. The file list below is therefore taken from `695b7ce`.

The source is api `origin/main` at `27dbc9c951f8da6623aabdfa709e31c346e141c3` plus this change's test-only diff. The platform pin is `a23afe320807e7c7775585a6246ca9981b337bb7` (`eng/platform-pin.json`, feed `0.0.0-alpha.0.hea7e5fc0aac4`). The tools were .NET SDK `11.0.100-rc.1.26425.128` and Stryker.NET `5.0.0` on Linux (4 cores). No production code changed.

## Configuration

Each file had its own scoped run: `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '<glob>' [--project Harborline.Foundation.Packs.csproj] --filter '<filter>'`. The script's generated config is the host `stryker-config.json` with `since` disabled, thresholds `{high 80, low 60, break 0}`, `mutate: ["<glob>"]` and `test-case-filter: "<filter>"`.

- `PackCompiledShapeCheck` and `PackInstaller` live in `packages/foundation-packs`, which the host tests reached only transitively. This change adds a direct `ProjectReference` to `apps/local-node-host/tests/tests.csproj`, as for `foundation-authorization`, so `--project Harborline.Foundation.Packs.csproj` can mutate it (AGENTS.md "One config mutates one direct reference"). `node eng/mutation-report.mjs --check` passes.
- For the large files the glob carries Stryker's `{start..end}` character spans (0-based, end-exclusive, BOM excluded) over the lines PR #251 added, so the scores describe the ck-1/ck-9 code rather than the whole file. The span's line range is in the table.
- Each file was run twice. The **before** run is on unchanged `main` tests, and the **after** run is on this change's tests. Program.cs and `RosterCrdtProjection.cs` were not rerun, because the first run of each had nothing left to kill. `Catalogue.cs` was not rerun either, because its only survivor is equivalent (see Survivors).

| Run | File (lines) | Filter | Before: K / S / NC, score | After: K / S / NC, score |
|---|---|---|---|---|
| ck1-pack-compiled-shape-check | `packages/foundation-packs/Install/Compatibility/PackAdmissionClassificationChecks.cs` (whole file) | `PackInstallRouteTests`, `ReleasedPackInstallRouteTests` (+ `PackCompiledShapeCheckTests` after) | 4 / 4 / 0, 50.00 | 8 / 0 / 0, **100.00** |
| ck1-pack-installer | `packages/foundation-packs/Install/PackInstaller.cs` (834-866) | `PackInstallRouteTests`, `ReleasedPackInstallRouteTests` | 6 / 2 / 0, 75.00 | 8 / 0 / 0, **100.00** |
| ck1-catalogue | `apps/local-node-host/Health/Catalogue.cs` (57-85, 635-643) | `CatalogueRouteTests` | 4 / 1 / 0, 80.00 | not rerun (equivalent survivor) |
| ck9-program-kernel-clock | `apps/local-node-host/Program.cs` (196) | `KernelClockIntegrationTests` | 0 tested (707 CompileError) | hand mutations H1, H2 |
| ck9-bootstrap-claim-expiry | `apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs` (475, 632-639) | `BootstrapClaimRedemptionTests`, `KernelClockIntegrationTests` | 8 / 5 / 0, 61.54 | 13 / 0 / 0, **100.00** |
| ck9-accounting-period-service | `apps/local-node-host/Data/Financial/NodeAccountingPeriodService.cs` (122-127) | `AccountingPeriodRouteTests`, `KernelClockIntegrationTests` | 0 / 2 / 0, 0.00 | 1 / 1 / 0, **50.00** (survivor equivalent) |
| ck9-accounting-period-route | `apps/local-node-host/Health/AccountingPeriodRoutes.cs` (74-87) | `AccountingPeriodRouteTests`, `KernelClockIntegrationTests` | 5 / 2 / 1, 62.50 | 7 / 1 / 0, **87.50** (survivor equivalent) |
| ck9-accounting-summary | `apps/local-node-host/Data/Financial/NodeAccountingSummaryService.cs` (49-53, 70-72) | `AccountingSummaryRouteTests`, `KernelClockIntegrationTests` | 1 / 1 / 0, 50.00 | 2 / 0 / 0, **100.00** |
| ck9-roster-record | `apps/local-node-host/Data/Roster/NodeRosterRecord.cs` (197-214) | `~Roster`, `KernelClockIntegrationTests` | 0 / 1 / 0, 0.00 | 1 / 0 / 0, **100.00** |
| ck9-roster-projection | `apps/local-node-host/Data/Roster/RosterCrdtProjection.cs` (569-572, 1003, 1015-1017, 1027-1028, 1169) | `~Roster`, `KernelClockIntegrationTests` | 5 / 0 / 0, **100.00** | not rerun |
| ck9-comms-projection | `apps/local-node-host/Data/Comms/CommsCrdtProjection.cs` (496-505) | `~Comms`, `KernelClockIntegrationTests` | 1 / 0 / 3, 25.00 | 4 / 0 / 0, **100.00** |
| ck9-comms-message | `apps/local-node-host/Data/Comms/NodeMessage.cs` (102-107) | `~Comms`, `KernelClockIntegrationTests` | 0 / 1 / 0, 0.00 | 1 / 0 / 0, **100.00** |

K is Killed, S is Survived and NC is NoCoverage. These are scoped runs over the named lines, not slice or whole-host scores.

### Mutants Stryker cannot test, and hand mutations

CompileError counts are project-wide rollbacks (AGENTS.md). Two cases matter for the ck-9 lines:

- **Program.cs.** One mutant in the top-level statements fails to compile (CS0165, unassigned `rootSeed`), so Stryker's safe mode rolls back every mutant in the generated `Main`, including line 196. The run tested nothing, so the registration is covered by hand mutations H1 and H2 instead (T-724 ruling 35).
- **`NodeRosterRecord.cs:203` and `:211`, `NodeMessage.cs:106`.** The conditional mutants on `TryParse(..., out var dto) ? dto : ...` are rolled back (CS0165), so the re-dating fallback is covered by hand mutations H5 and H6.

Stryker also generates no mutant for the spread order at `Catalogue.cs:642`, for the `.Where(TryParseIssuedAt)` filter at `RosterCrdtProjection.cs:571`, for the `IssuedAtOr` call sites, or for the clock sources. Those are hand mutations too.

Each hand mutation was applied to the production file, the host tests were built in Release, the filter below was run, and the file was restored from git.

| # | Site | Hand mutation | Result | Failing test(s) |
|---|---|---|---|---|
| H1 | `Program.cs:196` | registration of `KernelClock` removed | Red | every `KernelClockIntegrationTests.ProductionComposition_*` case, `Kernel_clock_reads_the_root_time_provider`, and `BootstrapClaimRedemptionTests.Composed_Seed_Profile_Accepts_Exactly_Its_Installer_Set` (composition cannot resolve `KernelClock`) |
| H2 | `Program.cs:196` | `new KernelClock(TimeProvider.System)`, a second clock | Red | `Kernel_clock_reads_the_root_time_provider` |
| H3 | `BootstrapClaimRedemption.cs:635` | `_kernelClock.IsExpired(claim.ExpiresAt)` → `now > claim.ExpiresAt` (expired after, not at) | Red | `Bootstrap_claim_is_expired_exactly_at_ExpiresAt` (expected `ClaimRejected`, got `Redeemed`) |
| H4 | `Catalogue.cs:642` | seed descriptors spread before `CompiledCatalogueType.All` | Red | **new** `Compiled_shapes_precede_the_platform_seed_types_in_declared_order` |
| H5 | `NodeRosterRecord.cs:214` | unparseable instant re-dated to `DateTimeOffset.UtcNow` | Red | `Unparseable_stored_instant_is_refused_not_redated`, **new** `Unparseable_stored_instant_refusal_names_the_instant` |
| H6 | `NodeMessage.cs:107` | unparseable instant re-dated to `DateTimeOffset.UtcNow` | Red | `Unparseable_stored_instant_is_refused_not_redated`, **new** `Unparseable_stored_instant_refusal_names_the_instant` |
| H7 | `RosterCrdtProjection.cs:571` | `.Where(s => NodeRosterRecord.TryParseIssuedAt(s, out _))` removed | Red | `RosterPartialAdoptionTests.MalformedDuplicateIsAuditedOnceAndFoldCompletes` |
| H8 | `RosterCrdtProjection.cs:1003` | `IssuedAtOr(candidate, now)` → `FromCrdtState(candidate).IssuedAtUtc` | Red | `RosterPartialAdoptionTests.MalformedDuplicateIsAuditedOnceAndFoldCompletes` |
| H9 | `RosterCrdtProjection.cs:1169` | same change in `GenesisRefusal` | Green: equivalent (see Survivors) | none (295 `~Roster` tests pass) |
| H10 | `NodeAccountingSummaryService.cs:72` | month from `DateTime.UtcNow` | Red | **new** `Summary_month_follows_the_host_clock_unless_a_day_is_given` |
| H11 | `NodeAccountingPeriodService.cs:126` | omitted date from `DateTime.UtcNow` | Red | `KernelClockIntegrationTests.Accounting_period_default_date_follows_the_host_clock` |
| H12 | `CommsCrdtProjection.cs:498` | parse gate never taken (`m.AuthoredAtIso is null && ...`) | Red | **new** `Peer_message_with_an_unparseable_instant_is_dropped_once_and_the_rest_lands` |

H3 first read green through the scripted harness right after a container restart. Rerun by hand against the whole `BootstrapClaimRedemptionTests` class, it failed as listed. Only the hand run's result is recorded here.

## Tests

The ADR-0103 ids are carried by `[Trait("Holds", "kernel-core-ck-1")]` and `[Trait("Holds", "kernel-core-ck-9")]` on the new ck tests. No test was renamed or removed. Each new test passed on the unchanged source. Each was proved red by the after run's kill of the mutant it targets, or by the hand mutation named above.

- ck-1:
  - `PackCompiledShapeCheckTests.Only_a_record_type_with_a_compiled_key_is_a_compiled_shape_claim` (a FormDefinition keyed `Record-Type` is not refused)
  - `PackCompiledShapeCheckTests.Compiled_shape_check_refuses_a_null_candidate_list_by_name`
  - `PackInstallRouteTests.Install_names_the_compiled_shape_refusal_ahead_of_an_earlier_standards_catalog_refusal`
  - `CatalogueRouteTests.Compiled_shapes_precede_the_platform_seed_types_in_declared_order`
- ck-9:
  - `BootstrapClaimRedemptionTests`:
    - `Claim_redeemed_after_its_issue_instant_inside_its_window_is_accepted`
    - `Claim_is_rejected_before_its_not_before_instant`
    - `Claim_whose_monotonic_lifetime_is_spent_is_rejected_though_the_wall_clock_was_set_back`
  - `AccountingPeriodRouteTests`:
    - `Open_for_an_explicit_date_opens_the_period_covering_that_date`
    - `Open_with_an_empty_date_string_defaults_like_an_omitted_date`
  - `AccountingSummaryRouteTests.Summary_month_follows_the_host_clock_unless_a_day_is_given`
  - `KernelClockIntegrationTests.Unparseable_stored_instant_refusal_names_the_instant`
  - `CommsCrdtConvergenceTests.Peer_message_with_an_unparseable_instant_is_dropped_once_and_the_rest_lands`
- Untagged, for survivors outside the ck lines in the same scoped files:
  - `PackCompiledShapeCheckTests.Transport_rule_check_refuses_a_null_candidate_list_by_name`
  - `PackCompiledShapeCheckTests.Destination_classifier_refuses_a_null_candidate_list_by_name`
  - `AccountingPeriodRouteTests.Open_with_an_unparseable_date_is_refused_as_invalid_date`

`CommsCrdtConvergenceTests.NewReplicaAsync` gained an optional logger so the drop can be observed. The parse gate runs before the signature gate, and signature verification catches the `FormatException` and returns false. So without the gate, the message would still be dropped, but by the signature gate with a second warning. The test therefore asserts that exactly one warning names the message and says its instant does not parse.

## Kill table (after runs)

Every mutant that the before runs left Survived or NoCoverage, with the test the after report names as its killer:

| File:line | Mutator | Mutant | Killing test |
|---|---|---|---|
| `PackAdmissionClassificationChecks.cs:22` | Statement | `ThrowIfNull(contents)` removed (transport rule) | `PackCompiledShapeCheckTests.Transport_rule_check_refuses_a_null_candidate_list_by_name` |
| `PackAdmissionClassificationChecks.cs:45` | Statement | `ThrowIfNull(contents)` removed (compiled shape) | `PackCompiledShapeCheckTests.Compiled_shape_check_refuses_a_null_candidate_list_by_name` |
| `PackAdmissionClassificationChecks.cs:49` | Logical | `Kind == RecordType && IsCompiledKey` → `\|\|` | `PackCompiledShapeCheckTests.Only_a_record_type_with_a_compiled_key_is_a_compiled_shape_claim` |
| `PackAdmissionClassificationChecks.cs:81` | Statement | `ThrowIfNull(contents)` removed (destination) | `PackCompiledShapeCheckTests.Destination_classifier_refuses_a_null_candidate_list_by_name` |
| `PackInstaller.cs:842` | Linq | `MinBy` → `MaxBy` | `PackInstallRouteTests.Install_names_the_compiled_shape_refusal_ahead_of_an_earlier_standards_catalog_refusal` |
| `PackInstaller.cs:844` | Unary | compiled-shape priority `-1` → `+1` | `PackInstallRouteTests.Install_names_the_compiled_shape_refusal_ahead_of_an_earlier_standards_catalog_refusal` |
| `BootstrapClaimRedemption.cs:635` | Logical | `now < IssuedAt && now < NotBefore` | `Claim_is_rejected_before_its_not_before_instant` |
| `BootstrapClaimRedemption.cs:635` | Equality | `now > claim.IssuedAt` | `Claim_redeemed_after_its_issue_instant_inside_its_window_is_accepted` |
| `BootstrapClaimRedemption.cs:635` | Equality | `now > claim.NotBefore` | `Claim_is_rejected_before_its_not_before_instant` |
| `BootstrapClaimRedemption.cs:638` | Logical | `elapsed >= 0 \|\| elapsed < lifetime` | `Claim_whose_monotonic_lifetime_is_spent_is_rejected_though_the_wall_clock_was_set_back` |
| `BootstrapClaimRedemption.cs:638` | Equality | `elapsed <= lifetime` | `Claim_whose_monotonic_lifetime_is_spent_is_rejected_though_the_wall_clock_was_set_back` |
| `NodeAccountingPeriodService.cs:126` | Null coalescing | `requestedDate ?? today` → `today` | `AccountingPeriodRouteTests.Open_for_an_explicit_date_opens_the_period_covering_that_date` |
| `AccountingPeriodRoutes.cs:77` | String | `IsNullOrWhiteSpace(Date)` → `Date != ""` | `AccountingPeriodRouteTests.Open_with_an_empty_date_string_defaults_like_an_omitted_date` |
| `AccountingPeriodRoutes.cs:81` | String | `"invalid_date"` → `""` (NoCoverage before) | `AccountingPeriodRouteTests.Open_with_an_unparseable_date_is_refused_as_invalid_date` |
| `NodeAccountingSummaryService.cs:72` | Null coalescing | `today ?? clock` → `clock` | `AccountingSummaryRouteTests.Summary_month_follows_the_host_clock_unless_a_day_is_given` |
| `NodeRosterRecord.cs:214` | String | `FormatException` message → `""` | `KernelClockIntegrationTests.Unparseable_stored_instant_refusal_names_the_instant` |
| `NodeMessage.cs:107` | String | `FormatException` message → `""` | `KernelClockIntegrationTests.Unparseable_stored_instant_refusal_names_the_instant` |
| `CommsCrdtProjection.cs:501` | Statement | drop warning removed (NoCoverage before) | `CommsCrdtConvergenceTests.Peer_message_with_an_unparseable_instant_is_dropped_once_and_the_rest_lands` |
| `CommsCrdtProjection.cs:502` | String | drop warning template → `""` (NoCoverage before) | `CommsCrdtConvergenceTests.Peer_message_with_an_unparseable_instant_is_dropped_once_and_the_rest_lands` |
| `CommsCrdtProjection.cs:504` | Statement | `continue` removed (NoCoverage before) | `CommsCrdtConvergenceTests.Peer_message_with_an_unparseable_instant_is_dropped_once_and_the_rest_lands` |

The ck mutants that the before runs already killed keep their killers in the after reports:

- `PackAdmissionClassificationChecks.cs:49` `!=` and `:53` pointer string: `Pack_install_refuses_a_record_type_that_claims_a_compiled_shape` and `Released_install_refuses_a_compiled_shape_claim`.
- `PackInstaller.cs:838` early-refusal condition: `Pack_install_refuses_a_record_type_that_claims_a_compiled_shape`, `Released_install_refuses_a_compiled_shape_claim` and `Check_collects_independent_refusals_without_mutating`.
- `Catalogue.cs:77`, `:80` (`Sealed: true`, provenance `"compiled"`): `Catalogue_types_lists_the_three_compiled_shapes_before_any_pack_is_installed`.
- `BootstrapClaimRedemption.cs:635` `|| IsExpired` → `&& IsExpired`: `Bootstrap_claim_is_expired_exactly_at_ExpiresAt` (before run).
- `RosterCrdtProjection.cs:1016` malformed refusal: `MalformedDuplicateIsAuditedOnceAndFoldCompletes`.
- `RosterCrdtProjection.cs:1027` receive window: `RosterPreInsertVerificationTests`.
- `CommsCrdtProjection.cs:498` parse condition: the `CommsCrdtConvergenceTests` convergence cases.

## Survivors

- **Equivalent: `ConfigureAwait(false)` → `true`.** This is `Catalogue.cs:638`, `NodeAccountingPeriodService.cs:127` and `AccountingPeriodRoutes.cs:87`. ASP.NET Core has no synchronization context, so the continuation runs the same either way, as T-537 records.
- **Equivalent: H9, `RosterCrdtProjection.cs:1169` (`GenesisRefusal`'s `IssuedAtOr`).** Every caller of `GenesisRefusal` passes only candidates that inbound verification accepted:
  - `ReportRejectedGenesis` (lines 855 and 897) receives `acceptedSnapshot` (line 636).
  - `ReconcileRefusalAuditAsync` iterates `acceptedSnapshot` when it has an anchor (line 637), and gets a null anchor at line 546.
  - Line 995 calls it only when the verification code is `roster.genesis.duplicate`.

  Line 1016 returns `roster.record.malformed` for every candidate whose instant does not parse, before any genesis check. So line 1169 never sees an unparseable instant, and `IssuedAtOr(candidate, now)` equals `FromCrdtState(candidate).IssuedAtUtc` there. A test that publishes a duplicate genesis with `IssuedAtIso = "not-a-date"` confirmed this: the only report was `roster.record.malformed`, on the original source and under H9 alike. That test was not kept. `IssuedAtOr` stays as a defensive fallback.

## Slice baselines

`eng/baselines/mutation-baseline.json` slices `data-identity-installation`, `data-roster-workflow`, `data-financial-comms`, `health-a-b`, `health-c-e` and `rest` stay `{"status": "pending"}`. `packages/foundation-packs` has no entry: it has no test project of its own, and the host project's baseline is kept per slice. The slice schema requires `{score, break, measured, run}`, where `run` is the Actions run id of a `mutation.yml` nightly slice run. These scoped local runs are not slice runs, so the ratchet rule has nothing to raise.

## Host suite

The filtered host run (`~Packs|~KernelClock|~Accounting|~Roster|~CatalogueRouteTests|~BootstrapClaimRedemptionTests|~CommsCrdtConvergenceTests|~ReleasedPackInstallRouteTests|~ArchTests`) on this source passed 1,011 of 1,012 tests in 4 minutes 48 seconds. Its local TRX is `apps/local-node-host/tests/TestResults/t909-verify.trx` (SHA-256 `805388cedd511867bffcc4146c29c3a28f7f74675016c23ef2e1209b176181f2`).

The one failure is environmental. `ControlHintDispatchFenceTests.Every_production_read_of_the_control_hint_is_an_allowed_pass_through` (T-664) scans the working tree and read the platform clone at `.platform/`, which the local feed build needs. It flagged only platform test files. With `.platform` moved aside, all 7 tests in that class passed. No test was renamed or removed, so no `policyRemovals` row is needed.

## Raw reports

The raw reports are archived on branch `archive/t909-host-mutation-reports-2026-09-29` under `reports/`, with `reports/SHA256SUMS`.

| Report | SHA-256 |
|---|---|
| `ck1-pack-compiled-shape-check.json` | `84734b7bd0b0c1445b059b3f2a97571166a16acef205844127fcde670eb59470` |
| `ck1-pack-compiled-shape-check-after.json` | `21370f187da04551f1cd05b0480fccf88ed60fb75d4db67e0ace95f367f2c98d` |
| `ck1-pack-installer.json` | `23fd8b3f7282fb6cae3e03fc0d596e7ffac3d04ebb2ddbae3c01d867f1d59bb2` |
| `ck1-pack-installer-after.json` | `ee8f6311aa7e0f5cf0bd1f7246919588a171849e57c79fc42b4ba5fdc0733d64` |
| `ck1-catalogue.json` | `a63aa14516bf082a94e3d080ae86f579698fb6c6a60c9b0a310b3a9ba8b076dd` |
| `ck9-program-kernel-clock.json` | `3bd44f8ea70fe0fc427ce30e8b78e0f1fa301743959bed0c7045ca57f4873535` |
| `ck9-bootstrap-claim-expiry.json` | `f651d2aecdd55d11c67cfcc632cfda311dc2045b8c98792f267c8aba43565da3` |
| `ck9-bootstrap-claim-expiry-after.json` | `140bbf3e3777649811b40939caf6aaea03b7fe5aa65bf65c90b1a981852cc418` |
| `ck9-accounting-period-service.json` | `73c52b70bf3ffdd0da6170c9690a811ced5f68fd953828561eb2ab186a6ebf0a` |
| `ck9-accounting-period-service-after.json` | `26679ebfb83c592172275514b2f596f501041e02965796504f71ea49ce322ec7` |
| `ck9-accounting-period-route.json` | `954cba656df128d269e30950434e54104d45eaa78317b1b80c5d481629ae9889` |
| `ck9-accounting-period-route-after.json` | `9c12d99c601ccbfc4916525ff8304788660dc2a0a3303084458c3a692c38f6ba` |
| `ck9-accounting-summary.json` | `6af2b9c6da6b205d54678d5e9afc84074187d96e625e0ddae3cad76d81a2999c` |
| `ck9-accounting-summary-after.json` | `6ee3f8ba9421334c3231f6bfed43c61b75bbd098e330fb41654b09a48030344d` |
| `ck9-roster-record.json` | `495da50cc4d414732a73aca5cb53c4ab288c7d12568b338aedced09ef3d7b1c3` |
| `ck9-roster-record-after.json` | `33d9429ab75bbc5af34f73fbe030599d943cbe7ce4c2524d9c9d12c76075f763` |
| `ck9-roster-projection.json` | `0e8fa14016f95734589f323db7c069a034acd6db398a5fdf09753b994df689b0` |
| `ck9-comms-projection.json` | `c1cdd6ba3495d84c3523ff1b8040b0692e5929f449e3238ea80dad0421e3259c` |
| `ck9-comms-projection-after.json` | `209a8a684ef2ee1fbb46d0d2730d21a2e18a0537b675689887aa595cb7aaffa8` |
| `ck9-comms-message.json` | `51f2315f7a6238dcb65d4ab1fe2693767564099f76a4e79fa8e5270ec2e9ccae` |
| `ck9-comms-message-after.json` | `e0bc9231b4c9cc4bede2b0c1cf0bd7fb76b9d4395319075786c874d40474f5c3` |
