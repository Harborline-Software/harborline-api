# ck-11 follow-up: gate entry-point refusal mutation evidence

This is scoped Stryker.NET evidence for two refusals in `packages/foundation-authorization/AuthorizationGate.cs` that the ck-11 roster-ejection run (`docs/evidence/ck11-roster-mutation-2026-09-29.md`) left surviving under its filter:

- the `Validate(request)` call in `DecideCoreAsync` (line 100), and
- the `DecideProspectiveAdministratorAsync` argument guard. The ticket cites lines 83-84; at this head the guard is lines 84-89.

This note covers only those spans. It does not certify the rest of `Validate` or `CanonicalTargetScope`.

## Setup

- Run: `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --project Harborline.Foundation.Authorization.csproj --scoped '**/AuthorizationGate.cs' --filter 'FullyQualifiedName~RosterGateDecisionTests|FullyQualifiedName~AttributionIsNotAuthorityTests|FullyQualifiedName~SelectedSessionPepTests|FullyQualifiedName~NodeAuthorizationRosterConstraintReaderTests|FullyQualifiedName~GateEntryPointRefusalTests'`. This is the ck-11 filter plus the new class.
- The run used Stryker.NET 5.0.0 from the tool manifest, on the pinned SDK `11.0.100-rc.1.26425.128`, with `.feed` built from platform `a23afe32` by `eng/build-local-feed.mjs`. It ran natively from a plain clone, not a linked worktree.

## New tests (`apps/local-node-host/tests/Authorization/GateEntryPointRefusalTests.cs`)

- `The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act` has three rows. Each row varies exactly one clause of the guard: `members:admit` on `members/handover`, `members:manage` on `members/invite`, and `members:manage` on `record/handover`. Each row asserts the guard's own message and `ParamName`, and that the closure was never read. Without the message assertion, the `record/handover` row would also pass when `Validate`'s record-kind refusal fires instead.
- `The_prospective_administrator_entry_point_decides_the_members_handover_act` shows that a stranger's real handover act reaches the closure and is Allowed. This is the positive leg, and it kills the `"members"`/`"handover"` → `""` mutants.
- `The_gate_refuses_a_malformed_act_or_target_before_reading_any_grant` has four rows: a non-canonical target scope, a non-canonical (ancestor) act scope, a ledger act on a plain record, and a tenant target naming another tenant. Each row asserts `Validate`'s own message and that the closure was never read. The closure allows everything, so only the shape check can refuse.

## Manual reds (before counting Stryker)

Each mutant was applied to the source, the new class was run, and the source was restored.

| Mutant | Red |
|---|---|
| `Validate(request);` removed | 4 rows of `The_gate_refuses_a_malformed_act_or_target...` |
| guard `Operation.Value !=` → `==` | 1 (`refuses_anything_but...`) |
| guard `RecordKind !=` → `==` | 1 |
| guard `RecordId !=` → `==` | 1 |
| first `\|\|` → `&&` | 2 |
| second `\|\|` → `&&` | 2 |
| `"members"` → `""` | `decides_the_members_handover_act` |
| `"handover"` → `""` | `decides_the_members_handover_act` |
| whole guard disabled (`if (false && (...))`) | all 3 rows of `refuses_anything_but...` |

## Stryker results for the claimed spans

| Line | Mutator | Status | Killing test (one named) |
|---|---|---|---|
| 84 | Logical (`\|\|` → `&&`, both positions) | Killed | GateEntryPointRefusalTests.The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 84 | Negate expression | Killed | The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 84 | Equality (`Operation.Value ==`) | Killed | RosterGateDecisionTests.A_prospective_successor_without_a_roster_edge_is_allowed_and_one_with_a_narrowed_edge_is_refused |
| 85 | Equality (`RecordKind == "members"`) | Killed | The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 85 | String (`""`) | Killed | The_prospective_administrator_entry_point_decides_the_members_handover_act |
| 86 | Equality (`RecordId == "handover"`) | Killed | The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 86 | String (`""`) | Killed | The_prospective_administrator_entry_point_decides_the_members_handover_act |
| 87 | Statement (throw removed) | Killed | The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 88 | String (message `""`) | Killed | The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act |
| 100 | Statement (`Validate(request)` removed) | Killed | The_gate_refuses_a_malformed_act_or_target_before_reading_any_grant |

No mutant in these spans survived, so none needed an equivalence argument.

For the whole file, the run reported Killed 89, Survived 70, NoCoverage 45, Ignored 22 and CompileError 6. That count covers the whole file; only the spans above are claimed.

## Left for other tickets

Under this filter, the remaining `Validate`/`CanonicalTargetScope` survivors and NoCoverage mutants are outside this ticket:

- the install-wide branch (271-282)
- `ThrowIfNullOrWhiteSpace` on kind/id (285-286)
- the `packages` pack/asset-type exception (290-292)
- the catalogue-field branch (296-302, 327)
- the record-id `/` refusal (336)

Some of them have tests in classes outside this filter (`InstallWideOperationTests` and the catalogue cases in `AuthorizationGateTests`), but that coverage was not measured here.

## Raw report

The JSON report (`.stryker/tests-scoped/reports/mutation-report.json`) embeds every host test source, so it is not tracked. SHA-256: `825E83C1C8EE5464E041EEC830B2FE7B41384C357BFC4CE12A06079224778BE5`.
