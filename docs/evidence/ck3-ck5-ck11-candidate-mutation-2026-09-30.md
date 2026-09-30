# ck-3, ck-5 and ck-11 candidate mutation re-run (T-984)

This note re-runs the DES-0029 mutation evidence that the T-984 release-candidate record (control `designs/DES-0029-kernel-core/supporting/release-candidate-2026-09-29.md`) lists as stale, for the ck-3, ck-5 and ck-11 items whose files no open API PR touches. The owner's T-984 Q1 ruling applies: an item carries only when every mutated file and every killing test file is byte-identical at the candidate, so a stale item is re-run at the candidate.

- api: `3a22265f44866e1d2c5410002f28ba493c747bf0` (origin/main when this lane started, the merge of api #300). This branch changes one test file only: `8f1caa30` adds one ck-11 test and `d882473e` tightens it (below). The six ck-11 runs up to `ck11-preinsert` and the resolver retry mutate files that neither commit touches. `t975-asset`, `t974-guards` and `t519-gate` ran at `8f1caa30`, and `ck11-rekey-final` at `d882473e`. No production code changed.
- platform: `0f30804948db81590c1032b88da9e18ad1dea0d6`, the commit `eng/platform-pin.json` names. Feed `0.0.0-alpha.0.hd54e30aa7af1`.
- Tools: .NET SDK `11.0.100-rc.1.26425.128`, Stryker.NET `5.0.0` from the tool manifest, winbox. `MSBUILDDISABLENODEREUSE=1` for every Stryker run and `-nodeReuse:false` for every hand build.
- Raw reports, configs, logs and hand-mutation diffs: orphan branch `archive/ck3-ck5-ck11-candidate-mutation-2026-09-30` (head `131a4e9e`), with `SHA256SUMS`.

## Scope

In scope, per the lane brief: ck-11 (api #285's six scoped runs and its hand mutation, api #246 and #276 hand mutations), ck-5 (T-975 `AssetRegistryRoutes.cs`, the T-519 `AuthorizationGate.cs` run and their hand mutations), ck-3 (T-974's create-route guard spans, and the #249 and #254 guard removals, except `JournalEntryRoutes.cs`).

Left out:
- `JournalEntryRoutes.cs` (its two T-974 guards and #249's journal removals): api #301 changes the file.
- api #252 and #268 (`AuthorizationWriteStageTests.cs`): api #282 changes it.
- Platform items (#176, #181, #190) and every `stryker-full` floor: not api-scoped runs.
- api #256 (`RosterGateDecisionTests.cs`): not in this lane's list.

One overlap to note: open api #308 (T-987, auto-merge armed) edits `RosterPreInsertVerificationTests.cs`, the killing test file of the ck-11 pre-insert run. When #308 merges, that item goes stale again under Q1. The change widens the draw bound of one test from 64 to 200,000 and reformats two initialisers. It does not change what any test asserts.

## Stryker runs

Every run used `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '<glob>' [--project <X.csproj>] --filter '<filter>'` from a real console. The generated config is the host `stryker-config.json` with `since` disabled, thresholds `{high 80, low 60, break 0}`, the named `mutate` glob and the test-case filter, and default per-test coverage. Each generated config is archived beside its report. Stryker ran one at a time. The host was heavily loaded by other lanes, including a platform Stryker run, throughout.

| Run | Item | Mutated | Filter (`FullyQualifiedName~`) | Killed | Survived | Timeout | NoCoverage | CompileError | Score |
|---|---|---|---|---:|---:|---:|---:|---:|---:|
| `ck11-reader` | ck-11 #285 | `**/Data/Identity/NodeAuthorizationRosterConstraintReader.cs` | `DesktopActorRosterIdentityTests`, `AdminTeamAccessAuthorityTests`, `RosterGateDecisionTests`, `AttributionIsNotAuthorityTests`, `NodeAuthorizationRosterConstraintReaderTests` | 5 | 3 | 1 | 0 | 2 | 66.67 |
| `ck11-resolver` | ck-11 #285 | `**/Data/Identity/SelectedSessionPermissionResolver.cs` | `SelectedSessionEffectivePermissionsTests`, `SelectedSessionPepTests` | 36 | 22 | 0 | 8 | 4 | 54.55 |
| `ck11-gate` | ck-11 #285 | `**/AuthorizationGate.cs` (`--project Harborline.Foundation.Authorization.csproj`) | `RosterGateDecisionTests`, `AttributionIsNotAuthorityTests`, `SelectedSessionPepTests`, `NodeAuthorizationRosterConstraintReaderTests` | 75 | 74 | 1 | 54 | 6 | 37.25 |
| `ck11-preinsert` | ck-11 #285 | `**/Data/Roster/RosterCrdtProjection.cs` | `RosterPreInsertVerificationTests` | 134 | 161 | 26 | 207 | 154 | 30.30 |
| `ck11-rekey` | ck-11 #285 | `**/Data/Authorization/RetiredDesktopActorRekey.cs` | `DesktopActorRekeyTests` | 8 | 10 | 3 | 0 | 7 | 52.38 |
| `ck11-rekey-8f1caa30` | ck-11 #285 | same, with the new test | same | 13 | 8 | 0 | 0 | 7 | 61.90 |
| `ck11-rekey-final` | ck-11 #285 | same, at `d882473e` | same | 14 | 7 | 0 | 0 | 7 | 66.67 |
| `ck11-operator` | ck-11 #285 | `**/Data/Identity/NodeOperatorIdentity.cs` | `DesktopActorRosterIdentityTests`, `DesktopActorRekeyTests`, `NodeOperatorIdentityTests` | 4 | 1 | 3 | 0 | 4 | 87.50 |
| `t975-asset` | ck-5 T-975 | `**/Health/AssetRegistryRoutes.cs` | `Harborline.Api.LocalNodeHost.Tests.AssetRegistry.` | 238 | 131 | 0 | 41 | 80 | 58.05 |
| `t519-gate` | ck-5 T-519 | `**/AuthorizationGate.cs` (`--project Harborline.Foundation.Authorization.csproj`) | `Harborline.Api.LocalNodeHost.Tests.Authorization.`, `Harborline.Api.LocalNodeHost.Tests.Identity.` | 187 | 13 | 0 | 4 | 6 | 91.67 |
| `t974-guards` | ck-3 T-974 | 24 character spans (below) | `refuses_a_client_supplied_record_id` | 94 | 0 | 1 | 0 | 0 in spans | 100 |

Scores are per mutated file. CompileError counts are Stryker's project-wide rollbacks, and Timeout counts as detected. Each scoped run mutates one whole file, so a file score counts code its filter never reaches. The ck-11 pre-insert score, for example, covers all 1,300 lines of `RosterCrdtProjection.cs`, of which pre-insert verification is lines 983-1123.

The first `ck11-resolver` run crashed after 1,383 s with no report. Stryker lost its vstest.console connections under host load ("failed to connect to vstest.console … after 90 seconds", then an unhandled exception while disposing its runners). The same command ran again after the other runs finished and completed in 449 s. The table shows that second run; the first run's log is archived.

### Mutant ids and line numbers

Mutant ids are the ids in each run's report. They change between runs, so match earlier notes by file, line and mutator. Line numbers are at `3a22265f` (`8f1caa30` changes no mutated file). `RosterCrdtProjection.cs` pre-insert lines sit 25 lines lower than in the 2026-09-29 ck-11 note: its line 1057 is 1082 here.

## ck-11 Roster

### Kill table

These are the silent-failure mutants the 2026-09-29 ck-11 note named: an ejected party keeps authority, tampered or ineligible evidence is admitted, or an unresolved binding confers authority.

| File:line | Id | Mutant | Silent failure | Result and killing test |
|---|---|---|---|---|
| `SelectedSessionPermissionResolver.cs:172` | 13654 | block removal of the final-epoch `return null` | a grant epoch advance landing mid-read still publishes the pre-advance set | Killed, `SelectedSessionPepTests.An_epoch_advance_landing_while_the_roster_loads_publishes_no_permissions` |
| `SelectedSessionPermissionResolver.cs:213` | 13672, 13673 | `(subject && id) \|\| active`; `(subject \|\| id) && active` | a session whose pinned grant is gone is carried by another live grant | Killed, `SelectedSessionPepTests.A_session_whose_pinned_grant_is_gone_is_not_carried_by_another_live_grant` |
| `SelectedSessionPermissionResolver.cs:161` | 13641 | `(count == 0 \|\| roles != null) && …` | a closure the gate refuses in full becomes an empty authenticated session | Killed, `SelectedSessionPepTests.A_closure_the_gate_refuses_in_full_is_unavailable_not_an_empty_session` |
| `SelectedSessionPermissionResolver.cs:144` | 13624 | ejection guard negated | an ejected subject keeps its grants | Killed, `SelectedSessionEffectivePermissionsTests.Resolved_empty_permissions_…` and others. Stryker makes no removal of the one-statement `if`; the hand removal is below. |
| `RosterCrdtProjection.cs:1117` | 25288 | `AuthorityFor` returns Owner for every party (conditional true) | an admitter or revoker without the permission is admitted | Timeout (detected). Hand check below. |
| `RosterCrdtProjection.cs:1089` | 25254 | conditional true (a revocation judged against `members:admit`) | same | Killed, `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation` |
| `RosterCrdtProjection.cs:1090` | 25257, 25259 | `(!contains && keyMismatch) \|\| …` and `\|\|` to `&&` | a revocation signed by one key in another party's name is stored | Killed, `A_revocation_signed_by_one_key_in_another_parties_name_never_reaches_the_durable_store` |
| `RosterCrdtProjection.cs:1069` | 25230, 25232 | conditional true; `rootHolder \|\| attesterTrusted` | a genesis no member of its chain attested is stored | Killed, `A_genesis_whose_receipt_no_member_of_its_chain_attested_never_reaches_the_durable_store` |
| `RosterCrdtProjection.cs:1094` | 25264 | `!AttesterIsTrusted` negated | a receipt signed by one key in the founder's name is trusted | Killed, `TamperedPermissionEvidenceConfersNoAuthorityThroughTheGate`; the message kill (25265) is `A_receipt_signed_by_one_key_in_the_founders_name_does_not_admit_the_record` |
| `RosterCrdtProjection.cs:1078` | 25241 | `evidence.Concat` to `Except` | a revocation in the same delivery does not eject its target | Killed, `A_revocation_in_the_same_delivery_refuses_the_revoked_members_later_admission` |
| `RosterCrdtProjection.cs:1080-1081` | 25242, 25246, 25247, 25248 | `< at` to `&&`, `== at` to `!= at`, nonce `\|\|` to `&&`, `CompareTo < 0` to `> 0` | a same-instant revocation with the lower nonce does not precede | Killed, `A_same_instant_revocation_with_the_lower_nonce_refuses_the_revoked_members_admission` |
| `RosterCrdtProjection.cs:1082` | 25251, 25252 | `== nonce` to `!= nonce`; `CompareOrdinal < 0` to `> 0` | a same-instant, same-nonce revocation whose signature sorts first does not precede | **Stryker: Survived.** Hand-applied, each identical mutation turned `A_same_instant_same_nonce_revocation_whose_signature_sorts_first_refuses_the_revoked_members_admission` red in 3 of 3 runs (`Assert.DoesNotContain` failed: the late admission was stored). See below. |
| `RosterCrdtProjection.cs:1010` | 25162 | `anchor ??=` to `anchor =` | a self-attested second root after an ordinary admission is stored | Killed, `A_self_attested_second_root_arriving_after_an_ordinary_admission_is_still_a_duplicate` |
| `RosterCrdtProjection.cs:1097` | 25271 | `partyMatches \|\| signatureMatches` | an admission the rebuilt chain dropped is stored | Killed, `A_second_admission_for_a_party_already_in_the_chain_never_reaches_the_durable_store` |
| `RetiredDesktopActorRekey.cs:42` | 2536 | `grants == 0 \|\| epochs == 0` | grant rows with no retired epoch row stay bound to the retired actor | Killed, `A_local_grant_row_with_no_retired_epoch_row_is_still_rekeyed_to_the_desktop_actor` |
| `NodeAuthorizationRosterConstraintReader.cs:36` | 12427 | `??` remove-left (the People binding lookup dropped) | an unresolved binding falls back wrongly | Killed, `AdminTeamAccessAuthorityTests.AdminSite_RecordsTheGateDecisionWithRosterInputs` |
| `NodeAuthorizationRosterConstraintReader.cs:26, 29, 31` | 12419, 12424, 12426 | registry-member recording | a principal is recorded as a registry member of the wrong team | Killed, `NodeAuthorizationRosterConstraintReaderTests` (`A_principal_with_no_team_registry_is_never_recorded_as_a_registry_member`, `Registry_membership_is_recorded_only_for_the_requested_team`) |
| `NodeOperatorIdentity.cs:34` | 13262 | `??` remove-right (retained admissions not read) | an ejected node party stops resolving, so the desktop plane is not refused as ejected | Killed, `DesktopActorRosterIdentityTests.An_ejected_node_party_is_refused_when_the_desktop_actor_asks_the_production_gate` |
| `NodeOperatorIdentity.cs:23` | 13257, 13258 | `roster is null`, `signer is null` | an operator is resolved without a roster or a signer | Killed, `DesktopActorRekeyTests.A_desktop_request_with_no_session_principal_is_attributed_to_the_founders_canonical_principal` and others |
| `AuthorizationGate.cs:107-108` | 366-370 | derived roster discarded; unreadable-roster fallback `Member`/`Ejected`/`RegistryMember` flipped | an unreadable roster confers membership | Killed, `NodeAuthorizationRosterConstraintReaderTests.An_unreadable_roster_confers_no_membership_and_the_gate_reads_the_principal_as_ejected`, `SelectedSessionPepTests.TwentyFive_Member_Roster_Rebuild_Is_Measured` |
| `AuthorizationGate.cs:190` | 408 | block removal of the ejected-empties-atoms block | an ejected principal keeps its atoms | Killed, `SelectedSessionPepTests.A_closure_the_gate_refuses_in_full_is_unavailable_not_an_empty_session` and others |
| `AuthorizationGate.cs:193` | 409, 410, 411 | the prospective-versus-member rule | a non-member passes as a prospective administrator | Killed, `RosterGateDecisionTests.A_prospective_successor_without_a_roster_edge_is_allowed_and_one_with_a_narrowed_edge_is_refused`, `AttributionIsNotAuthorityTests.Attribution_Alone_Never_Establishes_Authority` |
| `AuthorizationGate.cs:201-204` | 415-423 | the final coverage conjunction | a principal without member standing or grant coverage is allowed | Killed, the same `RosterGateDecisionTests` and `SelectedSessionPepTests` cases |

**The two 1082 survivors.** Stryker reported both Survived, but the same mutation applied by hand is killed deterministically by the test written for it. The test draws fresh keys until the admission's signature sorts after the revocation's, so the tie-break decides the outcome on every run. The likeliest cause is the run itself. Under host load, Stryker logged repeated `failed to connect to vstest.console` errors and cancelled test runs across several runs (the resolver run crashed on them; see above), and a mutant whose test run is cancelled mid-batch can be reported Survived. This note records the hand kill as the evidence for these two mutants and does not claim a Stryker kill. When api #308 merges, the draw bound is wider, and the Stryker re-run of this item should read them again.

**`RosterCrdtProjection.cs:1117` hand check.** Stryker reported Timeout (detected, not a named kill). By hand, the same mutation (the condition replaced by `true`) turned `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation` red, 1 of 27 in the class, and the source was restored.

### Survivor dispositions

The 2026-09-29 note's dispositions still hold, matched by text (pre-insert lines +25):

- **Equivalent.** Every `ConfigureAwait(false)` to `true` (reader 24, 25; rekey 33, 38, 41, 61, 63, 65; pre-insert 994; gate trace awaits). Reader 43, the refused-roster catch removed (Stryker substitutes the same `null`). Operator 34, `??` left-to-right (13260): the admission log holds every live member's key, so the result is the same live party or an ejected party with the same key, which the gate refuses. Pre-insert 997 (competing-root count), 1010:36 (conditional true), 1016:30, 1066 (`Append` to `Prepend` before `DistinctBy` over signatures, order-free), 1119:63 (`Guid.ToString("")` is `"D"`).
- **Fail-closed.** Pre-insert 1011 (no relaxation pass), 1044:54 (`>= 0` refuses every record carrying an unmapped-field map), 1080:25 `<=`, 1081:21 `||`, 1081:80 `<= 0` and 1082:25 `||` (each widens the preceding revocations), 1038 (a blank admitter is refused at 1090; a blank party makes signature verification throw).
- **Diagnostic or audit text only.** Pre-insert 1020-1023, 1026 (the refusal report text and permission label), 1051:20 and 1070 (a refusal code replaced by another non-null string, which still refuses).
- **Resolver** (lines +2 after 139, as the 2026-09-29 note's "After merging main" says). Equivalent: 112 and 113 (the first epoch check; the final fence at 170 re-reads it), 170 `(A && B) || C` and `A || (B && C)`, 201, 203 and 212 (`SingleOrDefault` to `Single`, or the no-pin `return null` removed: each throws inside the `try`, which returns null), 202 `&&` for `||`. Fail-closed: 105 (`ThrowIfNull` removed) and 180 (NoCoverage, the cancellation rethrow removed, so the resolver returns null). Audit or log text: 155:48 (the refusal-audit write) and 186-189. NoCoverage 39-57 is `NodeSelectedSessionAuthorizationEpochReader`, the database epoch reader in the same file, which these tests replace with a stub; the 2026-09-29 run had the same 8 NoCoverage.
- **Rekey.** Equivalent: 50:36 (removing `checked` differs only at `long.MaxValue`) and the `ConfigureAwait` mutants above.
- **Not dispositioned here, as on 2026-09-29.** Pre-insert 990 ordering (a convergence question, left to the roster CRDT convergence tests); pre-insert 1038 `A && B` for an unparseable `TeamId`; gate survivors outside the ejection path (request-shape validation, the prospective-Administrator entry check, trace strings); pre-insert NoCoverage 1106-1111 (`LocalAttestingParty`, the local publish path).

**New survivors that broke intended behaviour, now killed (rekey 24 and 54-58).** `RetiredDesktopActorRekey.cs` gained its audit entry in api #283 (T-986) after the ck-11 note. In `ck11-rekey` its payload survived: the dictionary initializer emptied (2548) and each key emptied (2549 `grantId`, 2550 `fromSubject`, 2551 `toSubject`). The rekey is an authorization change, and its audit entry records which grant moved from whom to whom. An entry without those fields is a silent failure. `NodeAuditOutboxTests.RetiredActorRekey_CommitsItsAudit` checks only the entry's actor.

- `8f1caa30` adds `DesktopActorRekeyTests.The_rekey_audit_is_typed_and_names_the_moved_grant_and_both_subjects` (`Holds=kernel-core-ck-11`). It boots a store holding a retired row and asserts all three fields of the staged entry. It passed on the unmutated tree. Removing each of the three entries by hand turned it red, 3 of 3, and the source was restored. In `ck11-rekey-8f1caa30` all four payload mutants are Killed by it.
- That run left one more survivor that breaks the audit: the event type emptied (line 24, id 2528; a Timeout in the first run). The test found its entry through the same constant, so it followed the mutation.
- `d882473e` makes the test look the entry up by the literal `AuthorizationGrantSubjectRekeyed`. It passed unmutated and went red with the type emptied by hand; the source was restored.
- In `ck11-rekey-final` (`d882473e`), the type (2528) and all four payload mutants are Killed by the test. The seven survivors left are the `ConfigureAwait` and `checked` mutants, all equivalent.

### Hand mutations

Each was applied in a second detached worktree at `3a22265f` (the rekey audit and operator rows at the commit they name, with the test file as it stands there), built in Debug with `-nodeReuse:false`, and the named tests were run. The file was then restored with `git checkout`. The unmutated control passed 101 of 101 across the filters used below.

| Item | Mutation | Tests run | Result |
|---|---|---|---|
| api #246 (as it stands after #247) | `NodeOperatorIdentity.PartyOf` returns `null` (the desktop actor's lookup of the party bound to the node key) | `DesktopActorRosterIdentityTests` | 2 of 3 red: `The_desktop_actor_reads_the_live_roster_edge_bound_to_this_nodes_key` and `An_ejected_node_party_is_refused_when_the_desktop_actor_asks_the_production_gate(presentDesktopActor: True)`. This is #246's "returning null from the lookup reds the same two". |
| api #246, ejected branch | `PartyOf` loses its `?? roster.EnumerateAdmissions()…` fallback, so a revoked node key no longer resolves | same | 1 of 3 red: `An_ejected_node_party_is_refused…(presentDesktopActor: True)` |
| api #246, reader | `var partyId = principal.Value;` in `NodeAuthorizationRosterConstraintReader.cs:36` | same | 0 of 3 red. api #247 moved #246's node-key mapping out of the reader into `NodeOperatorIdentity`, and these tests use a party reader that returns no binding, so this line is not their seam. The same mutant is Stryker id 12427, Killed by `AdminSite_RecordsTheGateDecisionWithRosterInputs` (above). |
| api #276, absence refuses | `if (inputs.Ejected \|\| !inputs.Member) return null;` at `SelectedSessionPermissionResolver.cs:144` | `SelectedSessionPepTests` | 3 of 21 red: `Deferred_Admission_With_Live_Grant_Is_Allowed` ("a live grant authorizes the deferred-admission principal when no roster admission exists"), `Known_powerless_role_resolves_empty_while_unknown_role_is_unavailable`, `Powerless_role_rejects_stale_cookie_and_accepts_fresh_epoch_selection`. The same three as #276. |
| api #276 and #285, ejection passes | line 144 (`if (inputs.Ejected) return null;`) removed | `SelectedSessionPepTests` | 3 of 21 red: `Ejected_Subject_With_The_Same_Live_Grant_Is_Refused` ("T-294 b3: the same live grant is refused once the subject's roster edge is ejected") and both cases of `Ejected_Gate_Principal_Is_Refused_When_Session_Presents_Canonical_Party`. |
| ck-11 pre-insert 1082 (`!= nonce`) | as in the kill table | `RosterPreInsertVerificationTests`, 3 runs | 1 of 27 red in each run: `A_same_instant_same_nonce_revocation_whose_signature_sorts_first_refuses_the_revoked_members_admission` |
| ck-11 pre-insert 1082 (`> 0`) | as in the kill table | same | the same, 3 of 3 |
| ck-11 pre-insert 1117 | `AuthorityFor` condition replaced by `true` (Owner for every party) | `RosterPreInsertVerificationTests` | 1 of 27 red: `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation` |
| ck-11 rekey audit, `8f1caa30` | each of `["grantId"]`, `["fromSubject"]`, `["toSubject"]` removed from the audit body (`RetiredDesktopActorRekey.cs:56-58`) | `The_rekey_audit_is_typed_and_names_the_moved_grant_and_both_subjects` | red, 3 of 3; control green |
| ck-11 rekey audit, `d882473e` | `RekeyedEventType = new("")` (line 24) | the same test | red; control green |
| ck-11 operator 23 | `(roster is not null \|\| signer is not null) && PartyOf(roster!.Current, signer!)` (Stryker 13256, Timeout; the first hand attempt without `!` did not compile) | `NodeOperatorIdentityTests` | 2 of 3 red: `A_roster_without_a_node_signer_has_no_operator`, `A_node_signer_without_a_roster_has_no_operator` |

## ck-5 Authorization

### T-975, `AssetRegistryRoutes.cs`

The named bypass mutants: each is the negated guard (`denied is not null` to `denied is null`), which lets a denied caller through.

| Write shape | Line | Id | Result | Named denial test among the killers |
|---|---:|---:|---|---|
| `POST /types` | 163 | 32250 | Killed (9 of 9 covering tests) | `AssetRegistryRouteTests.Types_Create_DeniedPackageAuthorCannotPersist` |
| `PUT /types/{id}` | 208 | 32280 | Killed (15 of 16) | `AssetRegistryRouteTests.Types_Update_DeniedPackageAuthorCannotPersist` |
| `POST /types/{id}/revert` | 290 | 32331 | Killed (3 of 3) | `AssetRegistryRouteTests.Types_Revert_DeniedPackageAuthorCannotPersist` |
| `POST /entities` with a client id | 392 | 32416 | Killed (2 of 2) | `AssetRegistryRouteTests.Entities_Create_DenialPrecedesClientSuppliedRecordIdRefusal` |
| `POST /entities`, unbound branch | 468 | 32453 | Killed (31 of 31) | `AssetRegistryRouteTests.Entities_Create_UnboundDeniedWriteCannotPersist` |
| `POST /edges` | 570 | 32502 | Killed (5 of 5) | `AssetRegistryRouteTests.Edge_Create_DeniedWriteCannotPersist` |

The permission-string mutants on the three type gates (`packages:author` emptied, lines 161, 206 and 288; ids 32248, 32278, 32329) are Killed. The entity and edge gates pass the `TeamRolePermissions.RecordsWrite` constant, which Stryker does not mutate.

The tenant-of-decision mutants (`??` remove-left, the route deciding in the active team's tenant instead of the selected principal's):

| Line | Id | Result and killing test |
|---:|---:|---|
| 152 | 32240 | Killed, `PackBoundSelectedPrincipalTests.Type_writes_decide_in_the_selected_tenant_when_the_active_team_differs` |
| 201 | 32277 | Killed, the same test |
| 283 | 32328 | Killed, the same test |
| 382 | 32401 | Killed, `PackBoundSelectedPrincipalTests.Canonical_principal_grant_authorizes_while_party_remains_record_author` |
| 564 | 32500 | Killed, `PackBoundSelectedPrincipalTests.Edge_write_decides_in_the_selected_tenant_when_the_active_team_differs` |

**Survivors on the write handlers (lines 150-316, 380-490, 562-603).** 51 Survived and 14 NoCoverage. None bypasses a gate. They fall in the T-975 note's groups, at the same lines:
- Equivalent: `ConfigureAwait(false)` to `true`.
- Response shape: error-code strings; `Results.Created` locations (187, 429, 482, 592); wire booleans `overridesSeed`, `seedExists`, `hasTenantRow` (189, 247, 265, 302); `EffectiveFrom` formatting (594); `ScanKey` trimming (423, 477).
- Input validation reachable only after the gate allows (156, 165, 210, 234, 384, 400, 574), plus the NoCoverage `JsonDocument.Parse("{}")` default at 414. These are follow-ups for the route owner, as the T-975 note says.

### T-519, `AuthorizationGate.cs`

The run took 3,848 s. The file score is 91.67% (187 Killed, 13 Survived, 4 NoCoverage, 0 Timeout), against 79.41% for the 2026-09-29 run. Lines are the same as in the T-519 note.

| Line | Id | Mutant | Result and killing test |
|---:|---:|---|---|
| 64 | 336 | membership-admission entry point `\|\|` to `&&` | Killed, `AuthorizationGateTests.Dedicated_entry_points_refuse_an_act_that_is_not_their_own` |
| 84 | 350 | prospective-administrator entry point `\|\|` to `&&` | Killed, the same test |
| 100, 101, 106, 136 | 362, 363, 365, 383 | `Validate` and the cancellation checks removed | Killed, `SharedRouteGuardPointOfUseTests.An_operation_that_is_not_declared_install_wide_refuses_without_a_record`, `AuthorizationTraceReadBoundaryTests.Http_trace_read_reaches_the_authorized_reader_and_the_reader_reaches_the_gate` |
| 107 | 366 | `derivedRoster ??=` to `=` | Killed, `DesktopActorRosterIdentityTests.The_desktop_actor_reads_the_live_roster_edge_bound_to_this_nodes_key` and others |
| 108 | 368, 369 | refuse fallback `Member: false` to `true`, `Ejected: true` to `false` | Killed, `RosterGateDecisionTests.Omitted_caller_roster_is_refused_when_the_gate_derives_no_member` and `NodeAuthorizationRosterConstraintReaderTests.An_unreadable_roster_confers_no_membership_and_the_gate_reads_the_principal_as_ejected` |
| 109-111 | 371-376 | the gate-owned `requireRosterMember` computation | Killed, `AdminTeamAccessAuthorityTests.After_The_Handover_The_Successor_Reaches_The_Admin_Surface_And_The_Predecessor_Does_Not`, `RosterGateDecisionTests.A_prospective_successor_without_a_roster_edge_is_allowed_and_one_with_a_narrowed_edge_is_refused`, `AccountSetupAcceptanceServiceTests.Selected_role_refusals_happen_before_mint` |
| 139 | 384, 385 | the Administrator exception `&&` to `\|\|`, and negated | Killed, `AuthorizationTraceReadBoundaryTests.Http_trace_read_…`, `AccountSetupAcceptanceServiceTests.Selected_role_refusals_happen_before_mint` |
| 147 | 388 | `RequireMember = false` to `true` | Killed, `AdminTeamAccessAuthorityTests.A_Refused_Handover_Leaves_Both_Parties_Admin_Access_Unchanged` |
| 232 | 439 | the attenuation refusal code emptied | Killed, `RosterGateDecisionTests.Role_attenuation_checks_each_required_scope_inside_the_gate`, `AccountSetupInvitationIssuerTests.Selected_role_attenuation_is_a_gate_refusal_with_evidence` |
| 277 | 481 | install-wide act off the install root: throw removed | Killed, `AuthorizationGateTests.DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead` |
| 300 | 502-505 | catalogue field and target disagree | Killed, `AuthorizationGateTests.Catalogue_field_target_that_disagrees_with_its_field_is_refused` and `Catalogue_field_target_uses_exact_coordinates_and_ordinary_grant_coverage` (NoCoverage on 2026-09-29) |
| 330 | 523 | a tenant target naming another tenant | Killed, `GateEntryPointRefusalTests.The_gate_refuses_a_malformed_act_or_target_before_reading_any_grant` |
| 332 | 526 | a tenant target's scope | Killed, `AuthorizationGateTests.DecideAsync_DecidesATenantTargetAtTheInstallRoot` |
| 336 | 528 | a record id with a scope separator: throw removed | Killed, `AuthorizationGateTests.DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead` |

The seven mutants the T-519 lane proved by hand (lines 64, 84, 277, 330, 332, 336) are each Killed by Stryker here, so this re-run needs no hand loop for them. Line 124 (`GrantRefusal = null`) has no Stryker mutator; its hand mutation M2 is below.

**Survivors and NoCoverage (17).** All fall in the T-519 note's groups, and none changes a verdict:
- Equivalent. `ConfigureAwait(false)` to `true` (42, 72, 91, 223). `ThrowIfCancellationRequested` removed after an already cancel-observing await (224). The dead store at 144. The `??` remove-left at 232:61, since `GrantRefusal` is cleared at 124 and only ever set to this constant. `RequiredPermissions.All` over the empty set (205, NoCoverage). The divergence-invariant throw at 175 (NoCoverage), unreachable by construction.
- Not a verdict. Line 41 lists candidate permissions (`InstallRootPermissionsAsync`); each is decided by `DecideAsync`. Exception message texts at 68, 278, 301 and 336:41; the exception types are asserted.
- Follow-up, outside T-519, as on 2026-09-29. Line 292 (`"asset-type"` emptied) is T-975's type-authoring admission, and its proofs live in `AssetRegistryRouteTests`, outside this filter. In the `t975-asset` run it is not mutated, because that run mutates the route file, not the gate.

### Hand mutations

| Item | Mutation | Tests run | Result |
|---|---|---|---|
| T-519 M2 | `GrantRefusal = null,` removed from the gate's request rewrite (`AuthorizationGate.cs:124`; Stryker has no mutator for it) | `RosterGateDecisionTests` | 1 of 25 red: `Caller_supplied_deny_is_ignored_and_only_gate_derived_denials_reach_the_decision` |
| T-975 tenant, line 152 | `…?.TenantId ?? NodeTenant.Resolve(activeTeam)` to `NodeTenant.Resolve(activeTeam)` | `PackBoundSelectedPrincipalTests` | 1 of 9 red: `Type_writes_decide_in_the_selected_tenant_when_the_active_team_differs` |
| T-975 tenant, line 201 | same shape | same | 1 of 9 red: the same test |
| T-975 tenant, line 283 | same shape | same | 1 of 9 red: the same test |
| T-975 tenant, line 564 | same shape | same | 1 of 9 red: `Edge_write_decides_in_the_selected_tenant_when_the_active_team_differs` |

## ck-3 IDs and identity

### T-974 guard spans

24 of the 26 T-974 create-route guards, as character spans of each `if` line plus its `return`, generated from the source at `3a22265f`. The two `JournalEntryRoutes.cs` guards are left out (api #301). Every mutant Stryker placed in the spans was detected: 94 Killed and 1 Timeout, 0 Survived and 0 NoCoverage. The console summary also printed 8 Timeouts, but only one mutant in the JSON report has that status (the T-974 note of 2026-09-29 saw the same).

| Guard | Bypass mutant | Id | Result and killing test |
|---|---|---:|---|
| `AccountingPeriodRoutes.cs:78` | `body?.Id is null` | 31929 | Killed, `AccountingPeriodRouteTests.Open_refuses_a_client_supplied_record_id` |
| `BankAccountRoutes.cs:179` | `body?.Id is null` | 33148 | Killed, `BankAccountCreateGateTests.Create_refuses_a_client_supplied_record_id` |
| `BillRoutes.cs:191` | `body.Id is null` | 33552 | Killed, `BillRouteTests.Create_refuses_a_client_supplied_record_id` |
| `CalendarCollectionRoutes.cs:87` | `body?.Id is null` | 33709 | Killed, `CalendarCollectionRouteTests.Create_refuses_a_client_supplied_record_id` |
| `ChartOfAccountsManagementRoutes.cs:174` | `body.Id is null` | 34811 | Killed, `ChartOfAccountsManagementRouteTests.Create_refuses_a_client_supplied_record_id` |
| `ChartOfAccountsRoutes.cs:89` | `\|\|` to `&&` | 34918 | Killed, `ChartOfAccountsRouteTests.Seed_refuses_a_client_supplied_record_id` |
| `CommsRoutes.cs:312` | `\|\|` to `&&` | 35013 | Killed, `CommsRouteTests.Post_refuses_a_client_supplied_record_id` |
| `CommsRoutes.cs:390` | `\|\|` to `&&` | 35038 | Killed, `CommsDmRouteScopeTests.Dm_append_refuses_a_client_supplied_record_id` |
| `ConsentRecordRoutes.cs:99` | `body?.Id is null` | 35462 | Killed, `ConsentRecordRouteAndSweepTests.Create_refuses_a_client_supplied_record_id` |
| `ContactRoutes.cs:227` | `\|\|` to `&&` | 35595 | Killed, `ContactDeleteRouteTests.Create_refuses_a_client_supplied_record_id` |
| `DocumentRoutes.cs:174` | `\|\|` to `&&` | 35982 | Killed, `DocumentRouteTests.Upload_refuses_a_client_supplied_record_id` |
| `DocumentTemplateRoutes.cs:176` | `\|\|` to `&&` | 36111 | Killed, `NodeDocumentTemplateRouteActingMemberPlacerTests.Issue_refuses_a_client_supplied_record_id` |
| `FormsRoutes.cs:170` | negate `TryGetProperty("instanceId")` | 37383 | Killed, `FormsRouteTests.Submit_refuses_a_client_supplied_record_id` |
| `InvoiceRoutes.cs:236` | `body.Id is null` | 38347 | Killed, `InvoiceRouteTests.Create_refuses_a_client_supplied_record_id` |
| `LeaseRoutes.cs:99` | `\|\|` to `&&` | 38953 | Killed, `LeaseRouteTests.Create_refuses_a_client_supplied_record_id` |
| `MaintenanceRoutes.cs:117` | `\|\|` to `&&` | 39555 | Killed, `MaintenanceRouteTests.Create_refuses_a_client_supplied_record_id` |
| `PayrollRoutes.cs:108` | `\|\|` to `&&` | 41070 | **Timeout** in Stryker. By hand, the same mutation turned both cases of `PayrollRouteTests.CreateEmployee_refuses_a_client_supplied_record_id` (`id`, `employeeId`) red. Each key's own Equality mutant (41072, 41073) is Killed by the same test. |
| `PayrollRoutes.cs:175` | `\|\|` to `&&` | 41122 | Killed, `PayrollRouteTests.CreatePayRun_refuses_a_client_supplied_record_id` |
| `PropertyRoutes.cs:107` | `\|\|` to `&&` | 41280 | Killed, `PropertyRouteTests.Create_refuses_a_client_supplied_record_id` |
| `RecurringInvoiceRoutes.cs:128` | `\|\|` to `&&` | 41353 | Killed, `RecurringInvoiceRouteTests.Create_refuses_a_client_supplied_record_id` |
| `RecurringInvoiceRoutes.cs:210` | `\|\|` to `&&` | 41418 | Killed, `RecurringInvoiceRouteTests.Generate_refuses_a_client_supplied_record_id` |
| `SchedulingDefinitionRoutes.cs:219` | `\|\|` to `&&` | 41961 | Killed, `SchedulingDefinitionRouteTests.Appointment_booking_refuses_a_client_supplied_record_id` |
| `SchedulingDefinitionRoutes.cs:277` | `\|\|` to `&&` | 42005 | Killed, `SchedulingDefinitionRouteTests.Event_create_refuses_a_client_supplied_record_id` |
| `WebSession/SelectedFormSubmitRoutes.cs:59` | negate `TryGetProperty("instanceId")` | 43883 | Killed, `FormsRouteTests.Selected_submit_refuses_a_client_supplied_record_id` |

In every two-key guard, both keys' Equality mutants and the negation are Killed as well. The refusal code emptied (`""`) is Killed in every guard.

### Hand mutations (#249 and #254 guard removals)

"Removing the refusal" was applied as `if (Environment.ProcessorCount < 0)`, which never refuses and still compiles without an unreachable-code warning.

| Item | Guard | Tests run | Result |
|---|---|---|---|
| api #249 | `SchedulingDefinitionRoutes.cs:219` (appointment booking) | `SchedulingDefinitionRouteTests` refusal tests | 2 of 4 red: both key cases of `Appointment_booking_refuses_a_client_supplied_record_id`; the event-create cases stay green |
| api #249 | `SchedulingDefinitionRoutes.cs:277` (event create) | same | 2 of 4 red: both key cases of `Event_create_refuses_a_client_supplied_record_id` |
| api #254 | `FormsRoutes.cs:170` | `FormsRouteTests` refusal tests | 1 of 2 red: `Submit_refuses_a_client_supplied_record_id`; the selected-submit test (a different guard) stays green |
| T-974 | `PayrollRoutes.cs:108`, `\|\|` to `&&` | `PayrollRouteTests.CreateEmployee_refuses_a_client_supplied_record_id` | 2 of 2 red |

Each removal fails only its own route's test, as #249 and #254 recorded.

## Results per item

| Row | Item | Result |
|---|---|---|
| ck-11 | api #285 scoped run, reader | **Holds.** `ck11-reader`: every named mutant Killed by its named test (kill table) |
| ck-11 | api #285 scoped run, resolver | **Holds.** `ck11-resolver` (retry): 172, 213 and 161 Killed by the three named `SelectedSessionPepTests`; ejection guard removal red by hand |
| ck-11 | api #285 scoped run, gate | **Holds.** `ck11-gate`: 107-108, 190, 193 and 201-204 Killed |
| ck-11 | api #285 scoped run, pre-insert | **Holds, with two hand kills.** `ck11-preinsert`: every named mutant Killed except 1082 `!= nonce` and `> 0` (Stryker Survived; red 3 of 3 by hand) and 1117 (Timeout; red by hand). Goes stale again when api #308 merges |
| ck-11 | api #285 scoped run, rekey | **Holds.** `ck11-rekey-final` at `d882473e`: 14 Killed, 7 Survived (all equivalent). Two new survivors (audit payload, audit type) were killed by the new test in this PR |
| ck-11 | api #285 scoped run, operator | **Holds.** `ck11-operator`: 34 remove-right and 23 null checks Killed; 23 `\|\|` (Timeout) red by hand |
| ck-11 | api #285 hand mutation (ejection guard) | **Holds.** 3 of 21 red |
| ck-11 | api #246 hand mutation | **Holds**, at the lookup's current home (`NodeOperatorIdentity.PartyOf`, since #247): 2 of 3 red, the same two as #246 |
| ck-11 | api #276 hand mutations | **Holds.** Both red, the same tests as #276 |
| ck-5 | T-975 scoped run (`AssetRegistryRoutes.cs`) | **Holds.** All six bypass mutants Killed by the named denial tests; all five tenant-of-decision mutants Killed |
| ck-5 | T-975 hand mutations (tenant 152, 201, 283, 564) | **Holds.** Each red |
| ck-5 | T-519 scoped run (`AuthorizationGate.cs`) | **Holds.** 91.67%; every named line Killed, including the seven the T-519 lane proved by hand |
| ck-5 | T-519 hand mutation M2 (line 124) | **Holds.** Red |
| ck-3 | T-974 guard spans (api #269) | **Holds for 24 of 26 guards.** 94 Killed and 1 Timeout (Payroll 108, red by hand). The two `JournalEntryRoutes.cs` guards stay **open**, pending api #301 |
| ck-3 | api #249 scoped and hand items | **Holds for SchedulingDefinitionRoutes** (spans Killed; both removals red). The `JournalEntryRoutes.cs` part stays **open** (api #301) |
| ck-3 | api #254 scoped and hand items (`FormsRoutes.cs`) | **Holds.** Span Killed; removal red |

## Archive

Orphan branch `archive/ck3-ck5-ck11-candidate-mutation-2026-09-30`, head `131a4e9e081bf1828004145b870facac926c1942`. `SHA256SUMS` there covers every file (its own SHA-256 is `67575755e64bd7ef81cb972fa88a1ba0d24ed3c3b153f07e5d13a3d2ef25e494`). The trx of the focused regression run (96 MB) is left out; its console log is kept.

| Report | SHA-256 |
|---|---|
| `reports/ck11-reader.mutation-report.json` | `4839011c9c475f93282a66fff8cc51fde91deeb856a5be13c84a56ded9761b97` |
| `reports/ck11-resolver.mutation-report.json` | `66ab9a5748639210beb723f0a125bc02deb0142cc7a9e58f5f910bb3fbbc30a6` |
| `reports/ck11-gate.mutation-report.json` | `7ee28ad599afcf31e09473ffc1439a8ba2d73adaddccefb912b3238c89770bc6` |
| `reports/ck11-preinsert.mutation-report.json` | `e92388e4336bc5170a307bfb9557be57a62609e162e12b7013ea96390b1acca0` |
| `reports/ck11-rekey.mutation-report.json` | `36349aa378435df9e576f3612ded4704d5b04cf3ec37c7b6dbb1f7ffb6d53dd2` |
| `reports/ck11-rekey-8f1caa30.mutation-report.json` | `10b27c57bdf270e61a0c8e7619013703e92f7fff33e5a1d7bc06e40ce0afa650` |
| `reports/ck11-rekey-final.mutation-report.json` | `551f97f8dfdfd6d2c3a9215e1e0735f13dd4f814886756ca155e540f5f5959e9` |
| `reports/ck11-operator.mutation-report.json` | `aca0163eeb244a3f3ab44c440caa9f0da0122843d195117ee83f7e0a8b964f7a` |
| `reports/t975-asset.mutation-report.json` | `0975ce8eac7476124922a7ec1a5577e00e9b0313b874927908e714a409701f33` |
| `reports/t519-gate.mutation-report.json` | `b56407b62c4623896e27d6528896b7eaa3a52673619434ff2d1e319970d2d4c0` |
| `reports/t974-guards.mutation-report.json` | `5c0f9280a719f13cd57076867e7f22dacca42e332ba67644ba64d4334aafdfe1` |

## Tests run

- At `8f1caa30`: host tests filtered to `.Authorization.`, `.Identity.`, `Roster`, `AssetRegistry`, `NodeAuditOutboxTests` and `refuses_a_client_supplied_record_id`: 1,849 passed, 18 skipped, 0 failed.
- At `d882473e`: the tightened test passed unmutated, and every Stryker initial test run passed.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
