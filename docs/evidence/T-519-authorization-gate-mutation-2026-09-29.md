# T-519 authorization gate, scoped mutation evidence

The seam is `AuthorizationGate` (`packages/foundation-authorization/AuthorizationGate.cs`). The claims under test:
- the gate derives the roster itself, and an unreadable roster refuses
- a caller cannot supply a grant refusal or roster switches
- the dedicated entry points cannot be borrowed for ordinary acts

The earlier T-519 lane recorded "every mutant CompileError". That was a misread. Its mutate glob `AuthorizationGate.cs{94..126}` is a character span, not a line range, so every compilable mutant was Ignored. The 73 CompileErrors were Stryker's project-wide rollbacks: pattern variables duplicated by instrumentation, and `Count()` to `Sum()` applied to a method group. The package is now mutable from the host tests through a direct `ProjectReference` (API PR #256).

The run used repository-pinned Stryker.NET 5.0.0 through `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/AuthorizationGate.cs' --project Harborline.Foundation.Authorization.csproj --filter 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Authorization.|FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Identity.'`. That filter selects 1,399 tests. The run used per-test coverage and the pinned SDK's `MSBuild.exe`, and took 4,889 s on a shared, loaded host. For the file, 158 mutants were Killed, 26 Survived, 16 NoCoverage and 4 Timeout, with 6 CompileError and 22 Ignored. The score is 79.41%. An earlier narrow run, using the T-519 lane's three test classes (68 tests, 147 s), gave 87 Killed, 70 Survived and 47 NoCoverage. The wider filter is the one that measures the gate.

## Named kills on the T-519 lines

| Line | Mutant | Killing test |
| ---: | --- | --- |
| 107 | `derivedRoster ??=` to `=`, which discards the derived roster | `AccountSetupInvitationIssuerTests.InvitationSites_RecordTheGateDecisionWithRosterInputs` |
| 108 | fallback `Ejected: true` to `false` (M1 of the T-519 lane) | `RosterGateDecisionTests.Omitted_caller_roster_is_refused_when_the_gate_derives_no_member` |
| 108 | `Member: false` to `true` in the refuse fallback | `RosterGateDecisionTests.Omitted_caller_roster_is_refused_when_the_gate_derives_no_member` |
| 109-111 | the gate-owned `requireRosterMember` computation: `!prospectiveAdministrator` negated, and `&&` to `\|\|` | `AdminTeamAccessAuthorityTests.After_The_Handover_The_Successor_Reaches_The_Admin_Surface_And_The_Predecessor_Does_Not`, `RosterGateDecisionTests.Omitted_caller_roster_is_refused_when_the_gate_derives_no_member`, `AccountSetupAcceptanceServiceTests.Selected_role_refusals_happen_before_mint` |
| 139 | the Administrator exception condition `&&` to `\|\|`, and negated | `AuthorizationTraceReadBoundaryTests.Http_trace_read_reaches_the_authorized_reader_and_the_reader_reaches_the_gate`, `AccountSetupAcceptanceServiceTests.Selected_role_refusals_happen_before_mint` |
| 147 | `RequireMember = false` to `true` in the Administrator exception | `AdminTeamAccessAuthorityTests.A_Refused_Handover_Leaves_Both_Parties_Admin_Access_Unchanged` |
| 232 | the attenuation refusal code emptied | `AccountSetupInvitationIssuerTests.Selected_role_attenuation_is_a_gate_refusal_with_evidence` |
| 100, 101, 106, 136 | `Validate` removed, and the cancellation checks removed | `AuthorizationGateTests.DecideAsync_RefusesWrongOperationRecordKindBeforeSnapshotRead`, `AuthorizationGateTests.DecideAsync_ObservesCancellationAfterSnapshotDependency` and others |

Stryker has no mutator for the `GrantRefusal = null` member of the `with` expression on line 124. The T-519 lane's manual mutation M2 (deleting it) remains that line's evidence; it was killed by `Caller_supplied_deny_is_ignored...`.

## Survivors and NoCoverage (42), classified

Meaningful gaps, now closed by new tests in `AuthorizationGateTests`:
- **Dedicated entry points, lines 64 and 84 (Logical `||` to `&&`).** `DecideMembershipAdmissionAsync` and `DecideProspectiveAdministratorAsync` would accept an act that is not theirs. `Dedicated_entry_points_refuse_an_act_that_is_not_their_own` has four cases: an ordinary record write, `members:read` on the members kind, `members:read` on the handover id, and `members:manage` on a non-handover id.
- **Validate: an install-wide act off the install root (line 277, NoCoverage).** Covered by `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead`, first case.
- **CanonicalTargetScope: a tenant target naming another tenant (line 330, NoCoverage).** Covered by `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead`, second case.
- **CanonicalTargetScope: a tenant target's scope (line 332, NoCoverage).** Covered by `DecideAsync_DecidesATenantTargetAtTheInstallRoot`.
- **CanonicalTargetScope: a record id with a scope separator (line 336, Survived).** Covered by `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead`, third case.

Each of these seven mutants was applied by hand, with the tests run and the source restored (scratchpad `ck5-t519-mutloop.mjs`). All seven failed the new tests and passed without the mutant. The tests fence behaviour that already existed, so their red is the mutant, as T-724 ruling 35 allows. All three new test methods assert that the closure was never read.

Equivalent or not a decision:
- **Line 144, `requireRosterMember = false` to `true`.** A dead store: the local is not read after line 144, and line 147, which makes the effective change, is Killed.
- **Line 232, `??` remove-left.** `GrantRefusal` is cleared to null on line 124 and only ever set to this one constant, so both sides are equal.
- **Line 205, `RequiredPermissions.All` (NoCoverage).** The gate sets `RequiredPermissions = PermissionSet.Empty` on line 121, so `All` over the empty set is always true.
- **Line 175, NoCoverage.** The divergence invariant throw; both readings come from the same atoms, so it is unreachable by construction.
- **Lines 72, 91 and 223, and line 224.** `ConfigureAwait(false)` to `true` is equivalent without a synchronization context. `ThrowIfCancellationRequested` removed after an already-cancel-observing await is a cancellation-timing change, not a verdict.
- **Lines 41 and 42.** `InstallRootPermissionsAsync` lists candidate permissions and produces no verdict; each candidate is decided by `DecideAsync`.
- **Lines 63 and 83, `ThrowIfNull(request)` removed.** A null request still fails closed, with a `NullReferenceException` on `request.Act` instead of an `ArgumentNullException`.
- **Lines 67-68 and 87-88 (NoCoverage).** These are the `throw` statements the new entry-point tests now reach.
- **Evidence text and ordering only; the verdict is unchanged.** These are follow-ups if evidence byte-stability is claimed:
  - line 107, the object initializer that drops `RegistryMember = false`, and line 108, `RegistryMember = false` to `true`, in the refuse fallback (the evidence line `registry:member:…` changes; the subject is ejected either way)
  - lines 227-228, the ordering of attenuation evidence
  - lines 382 and 385, the derivation and standing description strings
  - lines 295 and 306, the exception message texts

Follow-up, outside T-519:
- **Line 292 (`"asset-type"` emptied).** This is T-975's type-authoring admission. It survives this filter because its proofs live in `AssetRegistryRouteTests`, which the filter excludes.
- **Line 300, the catalogue field `||` to `&&`.** Its throw on line 301 is NoCoverage. This belongs to the catalogue field-target ticket.

The four Timeout mutants (lines 132, 135, 152 and 153) count as detected under Stryker's rules but are not behavioural kills.

## Raw reports

The raw reports are not kept in the tracked tree, for the size and scanner reasons in AGENTS.md "Mutation testing".

| Report | SHA-256 |
| --- | --- |
| Wide run (the one cited above) | `6EA5D5CA4853DA2F02F0BADC6E1803D40B966696734D010926F4164FB11B2313` |
| Narrow run | `84CF6FB49465D49A619024F3485878140D5033AD6C85C5BCDB199291A6558111` |

Archive both on an `archive/*` branch before citing them in DES-0029. This is scoped mutation evidence for the gate's decision path. It does not certify `WritePipeline` stage ordering, which is covered by the T-519 lane's manual mutation M4 and its stage-refusal theory.
