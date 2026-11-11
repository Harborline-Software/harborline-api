# T-519 gate and migrated executor proof

## Revision and scope

API base/main: `0a7a163c73c48779e8264f2ebeefe0b77915d4bc`. Control main fetched for this
reconciliation: `34a531b5d`; the complete `T-519/gate-and-executor-proof` record and
parent ticket were read from that ref, including the November coverage table and
October logs. Rule 13 found no open control PR changing this slice record.

This is a test/evidence change. Production, platform pins, mutation baselines and
the reviewed merge-unit exemption stay unchanged. The tests added here will be
main-bound evidence only after this draft merges (ADR-0103 decisions 1, 2, 7, 9).
This note does not close T-519 or claim implementation acceptance.

The oracle for order is ADR-0038's literal authorize/bind/mutate/validate/commit/react
sequence, also DES-0029 `kernel-core-ck-10` and `kernel-core-eng-10`. Executor and
record tests now compare against that literal rather than `WritePipeline.Order`.
Gate tests carry `Holds=kernel-core-eng-3`; the migrated pipeline fixtures carry
`Holds=kernel-core-ck-10`. Grant ids, actors, instants, expected verdicts and store
call counts are independent test inputs and literals, not production output used
as its own expected value (AGENTS.md, T-724 ruling 121).

## Acceptance 1: kernel-derived gate constraints

Tests in `apps/local-node-host/tests/Authorization/RosterGateDecisionTests.cs`:

| Restriction | Named test |
| --- | --- |
| Omitted roster refuses | `Omitted_caller_roster_is_refused_when_the_gate_derives_no_member` |
| Unreadable roster refuses an otherwise allowed ordinary act | `Unreadable_roster_refuses_an_ordinary_act_the_grants_allow` |
| False caller constraints cannot widen | `Caller_supplied_false_constraints_cannot_widen_the_gate_derived_roster` |
| Caller denial is cleared | `Caller_supplied_deny_is_ignored_and_only_gate_derived_denials_reach_the_decision` |
| A real kernel attenuation denial survives | `Role_attenuation_checks_each_required_scope_inside_the_gate` asserts denied scope cases and literal `authorization.grant.attenuation_failed` |
| Roster and denial setters are kernel-only | `Only_the_kernel_can_set_roster_facts_on_a_gate_request`, `Only_the_kernel_can_set_a_grant_refusal_on_a_gate_request` |
| All three decision entry points derive the same facts | `All_decision_entry_points_replace_forged_roster_and_deny_with_kernel_derivation` (ordinary, membership, prospective) |

`AuthorizationGateTests.Dedicated_entry_points_refuse_an_act_that_is_not_their_own`
also fences misuse of the dedicated entry points. The common `DecideCoreAsync`
reads the roster and replaces the caller observations; it does not trust them.
The entry-point theory asserts the resulting request's canonical party, ejection,
membership and cleared denial as well as the denied verdict.

Historical scoped receipts remain in
[T-519 authorization mutation](T-519-authorization-gate-mutation-2026-09-29.md) and
[ck-5 survivor dispositions](ck5-gate-survivors-2026-09-29.md). The latter's raw
after report is archived on `archive/ck5-gate-survivors-2026-09-29`, SHA-256
`3316b435b41a8fa4c5b1e09a6f40e47c960e569810ea42de01cba8ff416ab3f2`.
Its 183 Killed, 17 Survived, 4 NoCoverage, 6 file CompileError and 22 Ignored are
historical counts, not a fresh measurement of API main. The earlier 73 project
CompileErrors and character-span filtering are not kills. Neither are timeouts.

## Acceptance 2: executor and migrated adapters

All paths below are in `apps/local-node-host/tests/`.

| Path / plan | Named behavior tests |
| --- | --- |
| `Authorization/WritePipelineExecutorTests.cs` / S1 | `RunsTheSixStagesInTheDeclaredOrder`; six cases of `ARefusalStopsTheWriteAtItsStage`; `CommitAndReactReceiveExactlyTheSealedValue`; `ASettledBindStopsAfterBind` |
| `Authorization/AuthorizationWriteStageTests.cs` / original writer | `AuthorizationDefinitionWriter_EachStageRefusalStopsThereInAdr0038Order`; `AuthorizationDefinitionWriter_PersistsExactlyTheValidatedPayload` |
| `Authorization/AdmissionConferralPipelineTests.cs` / conferral | `Conferral_RunsEveryStageAndCommitsDefinitionsGrantAndAuditTogether`; `Conferral_EachStageRefusalStopsThereAndPersistsNothing` |
| `Entities/RecordWritePipelineTests.cs` / S2 | `Create_RunsTheSixStagesInOrder`; `Create_ValidationRefusal_StopsAtValidate`; `Update_RunsTheSixStages_AndRefusesAMissingRecordAtBind`; `Delete_RunsTheSixStages_AndAuditsTheAcceptedAct` |
| `Entities/CompiledSchemaEntityValidationTests.cs` / S2 existing oracle | `Writer_ValidatesTheTrimmedNameItStores` (literal trimmed name observed by validator and in stored result; unchanged file) |
| `Entities/HierarchySplitPipelineTests.cs` / S3 | `Split_RunsTheSixStages`; `Split_ARefusedReplacement_StopsAtValidate_BeforeAnyWrite_AndAValidRetrySucceeds` |
| `Entities/HierarchyMergePipelineTests.cs` / S3 | `Merge_RunsTheSixStages`; `Merge_AnUnwritableDisplacedChild_IsRefusedAtValidate_AndAnAllowedRetryMerges` |
| `Entities/HierarchyReparentPipelineTests.cs` / S3 | `Reparent_RunsTheSixStages`; `Reparent_UnderOwnDescendant_IsRefusedAtValidate_AndAValidRetrySucceeds` |
| `Forms/FormSubmissionWritePipelineTests.cs` / S3 | `Submission_RunsTheSixStages`; `Submission_DecisionForAnotherForm_StopsAtAuthorize` |
| `Definitions/DefinitionWritePipelineTests.cs` / S3b | `Form_TenantRegisterThenPublish_RunTheSixStages`; `Workflow_TenantRegisterThenPublish_RunTheSixStages`; decided/replayed wrong-definition/tenant/pack refusals; invalid-transition refusals |
| `Entities/BankAccountWritePipelineTests.cs` / S4 | `Create_RunsTheSixStages_AndStampsTheAdmittedInstant`; `Create_BlankDisplayName_IsRefusedAtValidate_AndARetrySucceeds`; `Archive_RunsTheSixStages`; `SetOpeningBalance_RunsTheSixStages` |
| `Identity/GrantRevocationWritePipelineTests.cs` / S4 | `Revoke_RunsTheSixStages`; `Revoke_DecisionForAnotherGrant_StopsAtAuthorize`; `Revoke_BackDatedEvidence_IsRefusedAtValidate_AndARetrySucceeds`; `Revoke_EvidenceForAnotherActor_IsRefusedAtValidate` |
| `Configuration/ConfigurationActivationPipelineTests.cs` / S5a | `Allowed_activation_runs_the_six_literal_stages_and_moves_the_effective_pointer`; `Refused_decision_stops_at_authorize_without_writing_pointer_or_evidence`; `An_allowed_activation_on_a_stale_baseline_is_refused_at_validate_without_writing` |
| `Packs/PackActivationPipelineTests.cs` / S5b | `An_allowed_activation_runs_the_six_stages_and_makes_the_version_active`; `Deactivation_runs_the_six_stages_and_leaves_the_pack_inactive`; `Narrowing_runs_the_six_stages_and_stores_the_override`; authorize and validate refusal cases |
| `Packs/PackInstallPipelineTests.cs` / S5c | `A_valid_install_runs_the_six_stages_and_the_version_is_installed`; `A_reconcile_with_no_projector_composed_fails_at_authorize_and_writes_nothing`; `A_reconcile_completes_every_pending_admission_and_retires_each_replayed_authority` |

Commit/react cancellation is pinned by `WritePipelineExecutorTests.CancellationAfterCommitStillRunsReact`
and `CancellationBeforeCommitStopsTheWrite`, bank
`Create_CancelledAfterCommit_ReturnsTheCommittedAccount_WhichStaysUsable`, configuration
`Cancellation_after_the_durable_switch_still_returns_the_committed_outcome` and
`Cancellation_reaching_evidence_publication_returns_the_committed_switch_and_leaves_the_row_owed`,
and pack `Cancellation_at_react_still_completes_the_committed_deactivation`.
The original writer's React case asserts a committed result. Admission conferral's
outer unit can still roll back on its own failed reaction; it is not claimed to
have crossed a durable outer commit merely because the inner stage was entered.

The original hand receipts are [API #302](https://github.com/Harborline-Software/harborline-api/pull/302)
(M1 skips authorize, M2 reacts before commit, M3 ignores settled bind, M4 omits
validate observation, M5-M7 planted bypasses, M8 drops conferral staging) and
[API #330](https://github.com/Harborline-Software/harborline-api/pull/330)
(actor/instant checks and post-commit cancellation red checks). These are historical
receipts for landed behavior, not fresh Stryker scores. The hierarchy report and
its outstanding 75 survivors, 3 uncovered sites and 20 compile errors remain
disclosed in [ck-10 hierarchy evidence](ck10-hierarchy-scoped-mutation-2026-10-03.md).

## Acceptance 3: October survivor reconciliation

| October item | Current disposition and named proof |
| --- | --- |
| Handover carried decision survives | Main contains `GrantWriterCarriedDecisionTests.Handover_DecisionForAnotherGrant_IsRefusedBeforeTheStore` |
| Admission narrowing carried decision survives | `GrantWriterCarriedDecisionTests.Narrow_DecisionForAnotherGrant_IsRefusedBeforeAnyRead` |
| Review / scope narrowing uncovered | `Review_DecisionForAnotherGrant_IsRefusedBeforeTheStore`, `NarrowScope_DecisionForAnotherGrant_IsRefusedBeforeTheStore` and matching-decision positive controls |
| Review actor and instant | Two `Review_AttributionNotMatchingTheDecision_IsRefusedBeforeTheStore` cases; exact `admittedDecision` parameter and zero store calls |
| Revocation actor and instant | `GrantRevocationWritePipelineTests.Revoke_EvidenceForAnotherActor_IsRefusedAtValidate`, `Revoke_BackDatedEvidence_IsRefusedAtValidate_AndARetrySucceeds` |
| Refused configuration decision id | `ConfigurationActivationPipelineTests.Refused_decision_stops_at_authorize_without_writing_pointer_or_evidence` now asserts literal SHA-256 `a754feca461e1b71873794fa3edc909ac50c5851f27168c962d3bf4649553d7f`; `Refused_decision_preserves_the_existing_effective_pointer_decision_id` pins retained durable evidence |
| Merge internal displaced child | `HierarchyCompositeWriteEffectsTests.Merge_OfARecordWithItsOwnChild_ReparentsOnlyOutsideChildren`; named kill in the hierarchy receipt, not compile-error invalidations |
| Reconcile completion count dropped through interface | Still a `Task` interface adapter discarding the implementation count. Open proposed follow-up outcome: **T-519/reconcile-completion-observability**, removal or observable return in a separately authorized implementation slice. No follow-up record is created by this API-only change; no blanket kill or equivalence claim |
| Configuration pointer-moved and transaction failure paths | No fresh mutation claim here; concurrency / crash-audit follow-up stays with T-1048. Bank HTTP idempotency stays with T-1047 |

[API #338](https://github.com/Harborline-Software/harborline-api/pull/338) landed at
`08302211f5f52018b4d0532b2f2a3d821509ee89`, an ancestor of this API main.
Its receipt records separate hand removals: review decision (1 red), review
attribution (2 red), scope decision (1 red), handover decision (1 red), admission
narrowing decision (1 red), restored 9/9 green. Thus the October inventory is
stale for these checks, rather than evidence that their source checks are absent.
This slice additionally links the four carried-decision and two review-attribution
cases from the allowed `GrantRevocationWritePipelineTests.cs` to ck-10.

The empty `NotYetOnTheExecutor` list is not universal durable-write coverage.
S6 belongs to `T-519/durable-write-map`. The merge unit opener remains its reviewed
exemption. No unrelated concurrency, crash-audit or HTTP-idempotency acceptance is
claimed here, and no residual survivor is upgraded to Killed by prose.

## Local validation

Pinned SDK: `11.0.100-rc.1.26425.128`. The repository's isolated public-clone
feed preparation built exact platform pin `22491171929b455ac09cdca4b01a8f65eb9c4039`;
the shared platform checkout was not modified. Restore used repository `nuget.config`
to avoid an unrelated machine-wide Telerik feed entering central package restore.

The affected host project built successfully. Release command:

```text
dotnet test apps/local-node-host/tests/tests.csproj --no-restore -c Release --filter 'Holds=kernel-core-ck-10|Holds=kernel-core-eng-3|FullyQualifiedName~GrantWriterCarriedDecisionTests|FullyQualifiedName~Tests.ArchTests.' --logger 'trx;LogFileName=t519-release.trx' --results-directory TestResults/t519 --verbosity quiet
```

Result: **773 passed, 0 failed, 0 skipped**. The earlier Debug run had 757 passes
and 16 architecture failures (construction proof and clock IL shapes); it is not
substituted for the passing Release receipt. `git diff --check` passed.

Fresh hand checks and the final restored/gate receipts follow below.

The 773-test Release TRX SHA-256 is
`3A8DC924F0E12E0430ED2DFC4BE21725FD715F748684C530FC7ABC3B6F9E2B70`.
The 16-check hand manifest SHA-256 is
`1D212ACB6084E9E7B295B9D02924F7421C7E354842CBCBF0B18AAB66CE9C84CF`.
Production diff checks after restoring all faults passed for `packages/` and
`apps/local-node-host/Data/`.

A separate current-source F1 hand fault replaced
`new ConfigurationActivationAuthority(false, decisionId)` with an empty id.
`ConfigurationActivationPipelineTests.Refused_decision_stops_at_authorize_without_writing_pointer_or_evidence`
failed, killing the loss of refused authority evidence. Log SHA-256:
`88bc68e840c94ca9ef4de069c82c8f60ebf97a15ca0ae9053791c15f37e3663f`.
Source was restored before the final green run. None of these 17 results is
a Stryker score, compile error, timeout or an unclassified survivor counted as a kill.

## Fresh hand-mutation receipts

Each fault was applied alone to the API base source with this test diff. Each test run rebuilt Release, read the resulting TRX for named failures, and restored the original source bytes in a finally block. Exit 1 alone was not counted as a kill. Raw logs and TRX are untracked under .stryker/t519-hand/.

| ID | Exact fault | Named failing test filter | Result | Log SHA-256 |
| --- | --- | --- | --- | --- |
| G1 | Fallback Ejected true to false | Unreadable_roster_refuses_an_ordinary_act_the_grants_allow | Killed | fa720d0e7102975e83ce8f21ee8006cd00995fe6a3f21b0915575a909d56efa9 |
| G2 | Prefer caller Roster over derivedRoster | Caller_supplied_false_constraints_cannot_widen | Killed | 1414625759ba27e033c68787ccf0d8570d4b3bca4b6c21be7a1794b94493f462 |
| G3 | Retain caller GrantRefusal | Caller_supplied_deny_is_ignored | Killed | 3f8df6e32de05d7ff89bf34beb216458829aabad3a3d466bea54b632675e07be |
| G4 | Make Roster init public | Only_the_kernel_can_set_roster | Killed | 6314f1c9ce1a181e8a75a8ed1f79d432e1687cb959e9d462999178386e409ecd |
| G5 | Make GrantRefusal init public | Only_the_kernel_can_set_a_grant_refusal | Killed | 9366c1b5fc3b41afe82c09cefc58d86618472defe9ba5f61ec5c709e1d348041 |
| C1 | Delete review decision check | Review_DecisionForAnotherGrant | Killed | fccc70c12b97a028bf4303d0eff90b4ca9573d223a8f8c05d9d4a7a1c5f7394f |
| C2 | Delete scope-narrowing decision check | NarrowScope_DecisionForAnotherGrant | Killed | 5f6fd1cf9e57063a71a7f5586cdf0e2baa2c785bcd84474ceaa431e6a980b703 |
| C3 | Disable review actor comparison | Review_AttributionNotMatchingTheDecision | Killed | b53ef0eb32d850f6ab35bcb08a94b32d23504521dc76017cca194473bc3af603 |
| C4 | Disable review instant comparison | Review_AttributionNotMatchingTheDecision | Killed | 7c3027ae81cc780acd5068180aaece6d1dd314ad9c33271899b205cbd44beb7c |
| C5 | Disable revoke actor comparison | Revoke_EvidenceForAnotherActor | Killed | d9e8a2c754f1ec9d350a2f975c0aaf46989d13b5079118a425c0716d4158b19a |
| C6 | Disable revoke instant comparison | Revoke_BackDatedEvidence | Killed | 3c216ee981fc708b10efe8c253871a1eac4fc09c0c02ee0c1ab9f03b43b7b53a |
| E1 | Skip executor AuthorizeAsync call | ARefusalStopsTheWriteAtItsStage | Killed | 8a5998ccf9d8649248118a5fd874d131b2ac95061eff1a3316b3545123ab3c1b |
| E2 | Observe request cancellation at React entry | CancellationAfterCommitStillRunsReact | Killed | b0a5568c07aeb416ff8453bb925e9643c303e1a5e49cf7f7359c66461b66ebe4 |
| E3 | Pass mutation instead of sealed value to commit | CommitAndReactReceiveExactlyTheSealedValue | Killed | 64b40c322963c443c75c8fdef6e4c799fff8fd5812ba7f7e862b344ed46c5eae |
| C-HandoverAsync | Delete handover decision check | Handover_DecisionForAnotherGrant | Killed | 091895e1676b8ea1de1897ce0b095dc55fb63b06c73050b4882910d1053a08fb |
| C-NarrowAsync | Delete admission-narrowing decision check | Narrow_DecisionForAnotherGrant | Killed | 06750105496203724dd4b89653f4329b2c39b1f973fcfd8a678014c59499a5fe |

## Restored confirmation

The final restored Release build passed with zero errors. The expanded suite passed **885 tests, 0 failed, 0 skipped**, including all ArchTests, the tagged pipeline and roster fixtures, AuthorizationGateTests, GrantWriterCarriedDecisionTests, CompiledSchemaEntityValidationTests and HierarchyCompositeWriteEffectsTests. It used the command above plus the last three additional class filters and --no-build after the explicit Release build.

Restored TRX SHA-256: 4F818B30B4DA779358197BB01236402F97B6846E7B4749B1AC13763E957EBA36.

Native gate execution follows on the clean committed candidate. No full native-gate pass is claimed by the focused suite.

## Native gate blocked: configured NAS registry

The unchanged `bash eng/verify.sh` ran against candidate tree
`70060d76d599d2970d22c2e8be62941a1584fb9d`. Both native focused modes passed:
**108 tests without coverage and 108 with coverage, zero failures**. Native
preflight reported `all prerequisites OK; quality=true; focused=passed`.

The gate then waited in the boundaries step's ESLint canary at
`pnpm install --frozen-lockfile --ignore-scripts` in `packages/contracts`.
A separate read-only `pnpm view @microsoft/eslint-formatter-sarif version`
through the unchanged registry reported:

```text
[WARN] GET https://nas.taildefd38.ts.net:4873/@microsoft%2Feslint-formatter-sarif error (ETIMEDOUT). Will retry in 10 seconds. 2 retries left.
```

The thread-owned stalled install and diagnostic were stopped after this
corroboration. The gate's own cleanup removed its temporary ESLint canary;
the tracked tree was clean afterward. This was an aborted dependency install,
not an ESLint assertion failure. Its final output was:

```text
eslint canary FAIL — pnpm install --frozen-lockfile failed in packages/contracts
FAILED: boundaries
  command: bash eng/verify-boundaries.sh
  No receipt written. Fix this and re-run: bash eng/verify.sh
```

Native log SHA-256:
`0CBAA84D50E0DADDF4A9D96F14CC79BD4FE7CD40E58F4C92C488D688EAA855EB`.
The process used the installed Git Bash and Rust tools, isolated pinned platform
and quality checkouts, the existing control checkout, and MSBuild's
`RestoreConfigFile` pointing to repository `nuget.config` to avoid a machine-wide
Telerik feed. No user config, registry, audit setup, gate or threshold was edited.

**Open execution prerequisite:** restore reachability of the configured NAS
registry, then rerun the full native gate. Rule 4 forbids changing that registry;
no registry fallback or gate waiver was taken. No full native-gate receipt,
push, draft PR, external review or PR check-watch is claimed. The tested changes
remain committed on the requested slice branch for resumption.
