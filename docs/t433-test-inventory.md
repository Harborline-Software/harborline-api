# T433 activation atomicity test inventory

## Measurement and identity audit

All cases below belong to `Harborline.Api.LocalNodeHost.Tests.dll` on Windows.

| Checkpoint | Discovered cases | Executed cases | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline `54325cb36721131213422ff30ff769df13f508cb` | 4,119 | 4,126 | 4,105 | 0 | 21 |
| Atomicity `13cb086f2bbdd445d2a12055376daded66f8aeb2` | 4,144 | 4,151 | 4,130 | 0 | 21 |
| Subsequent async tranche, direct Release host measurement | 4,147 | 4,154 | 4,133 | 0 | 21 |

The independent identity audit found 29 added and four replaced discovery identities:
25 genuinely new cases, four one-to-one semantic rewrites, and zero deleted tests.
The async tranche adds three further cases, listed separately below. The direct complete
Release host run measured 4,154 cases in 4m5s, with no failure or retry. Evidence:
`artifacts/t433-atomicity/async-full-host.log` and `async-full-host.trx` (SHA-256
`67b81dd3cd0b028de19bbd06579d2de731da086016008e7a92695c2680e17c57`).

Discovery-to-execution expands seven unchanged dynamic-theory rows: four from
`AuthorizationAdminRouteTests.EveryRoute...` and three from
`FormDraftRoutesWebPlaneFenceTests.EveryDraftRoute...`. These are not added or removed tests.

Independent normalized-inventory SHA-256 digests:

- Baseline: `4e7882b3d38bdb1d1417ad0a183c8f059c60badf257d7d07822673a166b1b20e`.
- Exact `13cb086f`: `7afae40d3414a1cfb6fd8c0cdb8482d0434cd048bb31ee5411427d1487a76484`.

Baseline execution evidence is the retained
`t427-detail-seed/artifacts/t427/final-host-tests.trx` (4,126 cases).
Exact atomicity execution evidence is
`artifacts/t433-atomicity/full-gate-third.log` and the corresponding
`.claude/gate-evidence/exact-clone-fail.json` at `13cb086f`; every exact-clone check
passed except the old count inventory. Discovery evidence is
`artifacts/t433-atomicity/discovery-baseline.log` and `discovery-current.log`.
The latter includes the three subsequent async cases; exclude the explicitly listed
three when comparing with exact `13cb086f`. VSTest discovery includes both
`TestDiscovery.TestFound` and `TestDiscovery.Completed.LastDiscoveredTests` batches.

## Twenty-five new atomicity cases

The fully qualified case identities include inline arguments so theory rows remain distinct.

```text
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Concurrent_catalogue_reader_sees_only_old_or_complete_new_replacement(refuse: False)
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Concurrent_catalogue_reader_sees_only_old_or_complete_new_replacement(refuse: True)
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Every_projected_store_reader_waits_for_the_activation_publication_barrier
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Forbidden_auditor_offer_refuses_activation_without_changing_platform
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Mixed_kind_late_failure_preserves_every_published_snapshot(failure: "cancel")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Mixed_kind_late_failure_preserves_every_published_snapshot(failure: "refusal")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Mixed_kind_late_failure_preserves_every_published_snapshot(failure: "throw")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Replacement_late_view_refusal_keeps_old_active_catalogue_and_publishes_no_early_view
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionDurableTransactionTests.Active_pointer_ownership_and_admission_restart_at_one_commit_boundary(commit: False)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionDurableTransactionTests.Active_pointer_ownership_and_admission_restart_at_one_commit_boundary(commit: True)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionDurableTransactionTests.Different_database_cannot_join_a_pack_activation
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionDurableTransactionTests.Pack_pointer_and_authorization_lifecycle_share_one_encrypted_commit(commit: False)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionDurableTransactionTests.Pack_pointer_and_authorization_lifecycle_share_one_encrypted_commit(commit: True)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Admission_reads_are_reentrant_across_await
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Cancellation_waiting_for_activation_does_not_acquire_or_leak_a_lease
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Committed_cleanup_failure_is_diagnostic_and_observers_run_once_outside_the_lease
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Concurrent_reader_observes_complete_committed_projection
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Concurrent_reader_observes_original_projection_after_exception
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Durable_commit_failure_discards_every_prepared_store
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Refusal_restores_original_references_and_enlists_shared_store_once
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Unenlisted_store_and_second_durable_unit_fail_closed
Harborline.Api.LocalNodeHost.Tests.Packs.PackReplacementRemovalTests.Competing_replacements_validate_expected_old_version_inside_activation_lease
Harborline.Api.LocalNodeHost.Tests.Packs.PackReplacementRemovalTests.Retirement_throw_restores_the_removed_definition_and_old_active_pointer
Harborline.Api.LocalNodeHost.Tests.Packs.T433CrossPackActivationAtomicityTests.Competing_different_packs_cannot_publish_against_a_stale_provider_slot_guard(durable: False)
Harborline.Api.LocalNodeHost.Tests.Packs.T433CrossPackActivationAtomicityTests.Competing_different_packs_cannot_publish_against_a_stale_provider_slot_guard(durable: True)
```

Category totals: Access mixed-kind/refusal/read-fence coverage 8; durable lifecycle/commit
coverage 5; barrier/transaction coverage 8; same-pack replacement/retirement coverage 2;
cross-pack provider-slot concurrency coverage 2.

## Four one-to-one semantic rewrites

These preserve the old scenario while replacing an obsolete partial-publication expectation
with all-or-nothing activation. Each is in namespace `Harborline.Api.LocalNodeHost.Tests`.

| Prior identity | Replacement identity |
| --- | --- |
| `Governance.CascadeDefaultsTests.Unsupported_legacy_projection_is_refused_and_retracts_superseded_values` | `Governance.CascadeDefaultsTests.Unsupported_legacy_projection_is_refused_and_preserves_admitted_values` |
| `Packs.PackReplacementRemovalTests.Crash_Mid_Admit_Is_Repaired_By_The_Next_Boot`, display “208 s2: a crash mid-admit is repaired by the next boot” | Same fully qualified method, display “208 s2: cancellation mid-admit discards the replacement and permits an explicit retry” |
| `Packs.PackSeedProjectionRouteTests.Malformed_type_is_skipped_valid_ones_project` | `Packs.PackSeedProjectionRouteTests.Malformed_type_discards_valid_siblings` |
| `Packs.TerminologyRuntimeTests.Malformed_overlay_clears_prior_runtime_content_and_refuses_reprojection` | `Packs.TerminologyRuntimeTests.Malformed_overlay_preserves_prior_runtime_content_and_refuses_reprojection` |

## Three later async cases

```text
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionTransactionTests.Committed_observer_failures_do_not_throw_or_skip_later_observers
Harborline.Api.LocalNodeHost.Tests.Packs.T433ActivationAsyncTests.Activation_awaits_observers_outside_the_lease_and_preserves_committed_success_on_failure
Harborline.Api.LocalNodeHost.Tests.Packs.T433ActivationAsyncTests.Canceled_activation_leaves_the_draft_and_admissions_unchanged
```

No failure allowances, skip identities, known-flaky identities, or retry policies changed.

## Four later deferred-health concurrency cases

These additions follow the complete green gate at `b9a5c771`; they are not included in its
4,154-case measurement. Both earlier inventory tranches remain unchanged.

```text
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Delayed_activation_health_refresh_cannot_erase_newer_active_findings(report: "workflow")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Delayed_activation_health_refresh_cannot_erase_newer_active_findings(report: "view")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.In_progress_health_refresh_fences_new_activation_until_its_snapshot_is_published(report: "workflow")
Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.In_progress_health_refresh_fences_new_activation_until_its_snapshot_is_published(report: "view")
```

The direct complete Release host run at `3962c3a92cc3cf24a1f0162d89cdef0ccb9379aa`
measured **4,158 total / 4,137 passed / 0 failed / 21 skipped** in 4m12s, with no retries.
`artifacts/t433-atomicity/deferred-health-full-host.trx` records the run from
`2026-09-16T02:48:46.0957298-04:00` to `2026-09-16T02:52:59.8208675-04:00`; its SHA-256 is
`498d39238e42e84e3637624bce91f5e697b4b12ffff28fea86c84025a1b171bf`.
The corresponding console log is `deferred-health-full-host.log` in the same directory.
This measurement, not arithmetic alone, supplies the updated Windows count tuple.

## Six PR 166 resource-lifetime regressions

These additions follow `40705a70`; they are not included in the earlier 4,158-case measurement.
All six first failed for the intended defect: three undisposed SQLite owner contexts and three
activation writers blocked by a paused enumerator. The same cases then passed after constructor
cleanup and snapshot-before-yield fixes. Evidence is retained as
`artifacts/t433-atomicity/review-resource-lifetime-{red,green}.log` and `.trx`.

```text
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Failed_sqlite_unit_initialization_disposes_its_owned_context(failureAt: "open")
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Failed_sqlite_unit_initialization_disposes_its_owned_context(failureAt: "begin")
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Failed_sqlite_unit_initialization_disposes_its_owned_context(failureAt: "enlist")
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Paused_form_enumerator_does_not_block_activation_and_retains_its_snapshot(publishedOnly: False)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Paused_form_enumerator_does_not_block_activation_and_retains_its_snapshot(publishedOnly: True)
Harborline.Api.LocalNodeHost.Tests.Packs.PackProjectionResourceLifetimeTests.Paused_standing_enumerator_does_not_block_activation_and_retains_its_snapshot
```

Three existing cases retain their assertions and coverage with corrected descriptions:

- `PackReplacementRemovalTests.Crash_Mid_Admit_Is_Repaired_By_The_Next_Boot` is renamed to
  `Cancellation_Mid_Admit_Is_Rolled_Back_And_Allows_Explicit_Retry`; its display name is unchanged.
- `PackSeedProjectionRouteTests.Invalid_workflow_admission_does_not_mutate_activation` is renamed
  to `Invalid_workflow_admission_leaves_the_pack_Draft`; its display name now describes refusal
  leaving a Draft rather than an already-Active workflow.
- `PackSeedProjectionRouteTests.Malformed_property_content_is_admission_gated` keeps its method
  name; its display name now describes complete activation refusal rather than successful activation.

These are one-to-one naming corrections, not removed tests. No skip/failure/retry allowance changes.
