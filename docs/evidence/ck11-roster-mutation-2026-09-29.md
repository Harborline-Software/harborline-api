# ck-11 roster authority mutation evidence

This note answers the DES-0029 `kernel-core-ck-11` Roster row, "Current roster code was not mutation verified" (T-291, T-294). It covers the roster's authority decisions: the gate's roster constraint reader, the ejection checks in the selected-session resolver and the authorization gate, roster pre-insert verification (signature, admitter authority and the no-escalation fence), the retired desktop-actor rekey, and the node operator identity.

The source is api `origin/main` at `27dbc9c` plus this change's test-only commits `158ae2f` and `f69aa5d`. No production code changed. The platform pin is `a23afe320807e7c7775585a6246ca9981b337bb7` (`eng/platform-pin.json`), packed by `node eng/build-local-feed.mjs` to feed version `0.0.0-alpha.0.hea7e5fc0aac4`. The SDK is the pinned `11.0.100-rc.1.26425.128` on Linux, with Stryker.NET 5.0.0 from the tool manifest.

## Tests

New tests carry `[Trait("Holds", "kernel-core-ck-11")]`. No existing test was renamed or removed.

- `NodeAuthorizationRosterConstraintReaderTests` (new): `A_principal_with_no_team_registry_is_never_recorded_as_a_registry_member`, `Registry_membership_is_recorded_only_for_the_requested_team` (4 cases), `An_unreadable_roster_confers_no_membership_and_the_gate_reads_the_principal_as_ejected`
- `SelectedSessionPepTests`: `An_epoch_advance_landing_while_the_roster_loads_publishes_no_permissions`, `A_session_whose_pinned_grant_is_gone_is_not_carried_by_another_live_grant`, `A_closure_the_gate_refuses_in_full_is_unavailable_not_an_empty_session`
- `RosterPreInsertVerificationTests`: `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation`, `A_revocation_signed_by_one_key_in_another_parties_name_never_reaches_the_durable_store`, `A_genesis_whose_receipt_no_member_of_its_chain_attested_never_reaches_the_durable_store`, `A_receipt_signed_by_one_key_in_the_founders_name_does_not_admit_the_record`, `A_revocation_in_the_same_delivery_refuses_the_revoked_members_later_admission`, `A_same_instant_revocation_with_the_lower_nonce_refuses_the_revoked_members_admission`, `A_self_attested_second_root_arriving_after_an_ordinary_admission_is_still_a_duplicate`, `A_second_admission_for_a_party_already_in_the_chain_never_reaches_the_durable_store`, `A_member_holding_revoke_but_not_admit_cannot_store_an_admission`, `A_same_instant_same_nonce_revocation_whose_signature_sorts_first_refuses_the_revoked_members_admission`
- `DesktopActorRekeyTests`: `A_local_grant_row_with_no_retired_epoch_row_is_still_rekeyed_to_the_desktop_actor`
- `NodeOperatorIdentityTests` (new): `A_roster_without_a_node_signer_has_no_operator`, `A_node_signer_without_a_roster_has_no_operator`, `The_operator_is_the_party_bound_to_the_node_key`

Every new test passed on the unmutated tree before any mutation was applied.

## Configuration

Each file had its own scoped run: `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '<glob>' [--project X.csproj] --filter '<classes>'`. `AuthorizationGate.cs` lives in a package, so its run adds `--project Harborline.Foundation.Authorization.csproj`; the host test project references that package directly. The baseline runs used the existing test classes on `27dbc9c`. The final runs used the same filters plus the new classes: `NodeAuthorizationRosterConstraintReaderTests` for the reader and gate runs, and `NodeOperatorIdentityTests` for the operator run.

| File | Filter (FullyQualifiedName~) |
|---|---|
| `**/Data/Identity/NodeAuthorizationRosterConstraintReader.cs` | `DesktopActorRosterIdentityTests`, `AdminTeamAccessAuthorityTests`, `RosterGateDecisionTests`, `AttributionIsNotAuthorityTests` |
| `**/Data/Identity/SelectedSessionPermissionResolver.cs` | `SelectedSessionEffectivePermissionsTests`, `SelectedSessionPepTests` |
| `**/AuthorizationGate.cs` (`--project Harborline.Foundation.Authorization.csproj`) | `RosterGateDecisionTests`, `AttributionIsNotAuthorityTests`, `SelectedSessionPepTests` |
| `**/Data/Roster/RosterCrdtProjection.cs` | `RosterPreInsertVerificationTests` |
| `**/Data/Authorization/RetiredDesktopActorRekey.cs` | `DesktopActorRekeyTests` |
| `**/Data/Identity/NodeOperatorIdentity.cs` | `DesktopActorRosterIdentityTests`, `DesktopActorRekeyTests` |

## Per-file scores

Each run mutates one whole file. `RosterCrdtProjection.cs` is 1,343 lines, of which pre-insert verification (`VerifyBeforeInsertAsync`, `VerifyInboundRecord`, `AuthorityFor`, `AttesterIsTrusted`, lines 956-1100) is one part. Its whole-file score therefore counts code this filter never reaches (hydration, publish, supersession, administrator removal). These are scoped runs of one file each, not slice or whole-host scores. CompileError counts are Stryker's project-wide rollbacks, and Timeout counts as detected.

| File | Run | Score | Killed | Survived | Timeout | NoCoverage | CompileError |
|---|---|---:|---:|---:|---:|---:|---:|
| `NodeAuthorizationRosterConstraintReader.cs` | baseline | 22.22 | 2 | 3 | 0 | 4 | 2 |
| | final | 55.56 | 5 | 4 | 0 | 0 | 2 |
| `SelectedSessionPermissionResolver.cs` | baseline | 48.48 | 32 | 25 | 0 | 9 | 4 |
| | final | 54.55 | 36 | 22 | 0 | 8 | 4 |
| `AuthorizationGate.cs` | baseline | 35.78 | 73 | 77 | 0 | 54 | 6 |
| | final | 36.76 | 75 | 75 | 0 | 54 | 6 |
| `RosterCrdtProjection.cs` | baseline | 25.14 | 121 | 185 | 10 | 205 | 154 |
| | final | 28.60 | 139 | 170 | 10 | 202 | 154 |
| `RetiredDesktopActorRekey.cs` | baseline | 50.00 | 8 | 8 | 0 | 0 | 7 |
| | final | 56.25 | 9 | 7 | 0 | 0 | 7 |
| `NodeOperatorIdentity.cs` | baseline | 62.50 | 5 | 3 | 0 | 0 | 4 |
| | final | 75.00 | 6 | 2 | 0 | 0 | 4 |

## Kill table

These are the silent-failure mutants: an ejected party keeps authority, tampered or ineligible evidence is admitted, or an unresolved binding confers authority. Each was Survived or NoCoverage in the baseline. Each is Killed in the final report by the named test, and, except where noted, a manual mutation of the same shape turned that test red first. Mutant ids change between runs, so the table matches by file, line and mutator.

| File:line | Mutant | Silent failure | Killing test |
|---|---|---|---|
| `SelectedSessionPermissionResolver.cs:170` | block removal of the final-epoch `return null` | a grant epoch advance landing mid-read still publishes the pre-advance set | `An_epoch_advance_landing_while_the_roster_loads_publishes_no_permissions` |
| `SelectedSessionPermissionResolver.cs:211` | `(subject && id) \|\| active` | a session whose pinned grant is gone is carried by another live grant | `A_session_whose_pinned_grant_is_gone_is_not_carried_by_another_live_grant` |
| `SelectedSessionPermissionResolver.cs:211` | `(subject \|\| id) && active` | same | same (Stryker kill; the manual probe dropped `&& active` and was not this exact shape) |
| `SelectedSessionPermissionResolver.cs:159` | `(count == 0 \|\| roles != null) && …` | a closure the gate refuses in full becomes an empty authenticated session | `A_closure_the_gate_refuses_in_full_is_unavailable_not_an_empty_session` |
| `RosterCrdtProjection.cs:1092` | `AuthorityFor` returns Owner for every party | an admitter or revoker without the permission is admitted (no-escalation) | `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation` |
| `RosterCrdtProjection.cs:1064` | conditional true; `admission is null` | a revocation is judged against `members:admit` | `A_member_holding_admit_but_not_revoke_cannot_store_a_revocation` |
| `RosterCrdtProjection.cs:1065` | `(!contains && keyMismatch) \|\| …` | a revocation signed by one key in another party's name is stored | `A_revocation_signed_by_one_key_in_another_parties_name_never_reaches_the_durable_store` |
| `RosterCrdtProjection.cs:1044` | conditional true; `rootHolder \|\| attesterTrusted` | a genesis no member of its own chain attested is stored | `A_genesis_whose_receipt_no_member_of_its_chain_attested_never_reaches_the_durable_store` |
| `RosterCrdtProjection.cs:1098` | `partyMatches \|\| keyMatches` | a receipt signed by one key in the founder's name is trusted | `A_receipt_signed_by_one_key_in_the_founders_name_does_not_admit_the_record` |
| `RosterCrdtProjection.cs:1053` | `evidence.Except(incoming)` | a revocation in the same delivery does not eject its target | `A_revocation_in_the_same_delivery_refuses_the_revoked_members_later_admission` |
| `RosterCrdtProjection.cs:1056` | `== at` to `!= at`; `CompareTo < 0` to `> 0`; `\|\|` to `&&` | a same-instant revocation with the lower nonce does not precede | `A_same_instant_revocation_with_the_lower_nonce_refuses_the_revoked_members_admission` |
| `RosterCrdtProjection.cs:1057` | `CompareOrdinal < 0` to `> 0`; `== nonce` to `!= nonce` | a same-instant, same-nonce revocation whose signature sorts first does not precede | `A_same_instant_same_nonce_revocation_whose_signature_sorts_first_refuses_the_revoked_members_admission` (the `!=` kill is Stryker's) |
| `RosterCrdtProjection.cs:985` | `anchor ??=` to `anchor =` | a self-attested second root after an ordinary admission is stored | `A_self_attested_second_root_arriving_after_an_ordinary_admission_is_still_a_duplicate` |
| `RosterCrdtProjection.cs:1072` | `partyMatches \|\| signatureMatches` | an admission the rebuilt chain dropped is stored because its party is present | `A_second_admission_for_a_party_already_in_the_chain_never_reaches_the_durable_store` |
| `RetiredDesktopActorRekey.cs:37` | `grants == 0 \|\| epochs == 0` | grant rows with no retired epoch row stay bound to the retired actor | `A_local_grant_row_with_no_retired_epoch_row_is_still_rekeyed_to_the_desktop_actor` |

The ejection guards themselves were already killed in the baseline. In the resolver, `if (inputs.Ejected) return null;` gets only a Negate mutant from Stryker, which is killed. Stryker makes no removal of a one-statement `if` (the T-974 pattern), so the guard was removed by hand. `Ejected_Gate_Principal_Is_Refused_When_Session_Presents_Canonical_Party` (both cases) went red, and the source was restored. In the gate, the unreadable-roster default's `Member: false` and `Ejected: true` (line 108), the ejected-empties-atoms block (line 190), the prospective-versus-member rule (line 193) and the final coverage conjunction (lines 201-204) are Killed by `RosterGateDecisionTests` and `AttributionIsNotAuthorityTests`. `!(roster.Ejected)` (line 189) is a CompileError rollback. In the reader, the unresolved People binding falling back to the principal (line 36) is Killed by `AdminSite_RecordsTheGateDecisionWithRosterInputs`. The pre-insert signature check (lines 1031-1034) is Killed by `InvalidSignatureNeverReachesDurableStore` and the new tests.

These non-authority survivors were also killed, each proven red by hand:

- Reader lines 26, 29 and 31, and gate lines 107-108 (`RegistryMember` in the decision evidence), by the reader tests.
- `NodeOperatorIdentity.cs:23` (`roster != null || signer != null` throws instead of answering null) by `NodeOperatorIdentityTests`.

## Survivor dispositions

Every remaining survivor in the listed decision paths falls in one of the groups below. None lets an ejected party keep authority, admits tampered evidence, or lets an unresolved binding confer authority.

**Equivalent.**

- Every `ConfigureAwait(false)` to `true` Boolean mutant. The host has no synchronization context, so the continuation and result are unchanged.
- Resolver 112 and 113 (the first epoch check weakened or removed). The final fence at line 168 re-reads the epoch and requires it to equal both the pinned and the first-read value, so the outcome is unchanged. Resolver 168 `(A && B) || C` and `A || (B && C)`: after line 112, `epoch == pinned`, so `B` and `C` are the same test. An earlier probe for the `&&` mutant dropped `C` and went red. That probe was not Stryker's shape, and Stryker's mutant is equivalent.
- Resolver 199, 201 and 210 (`SingleOrDefault` to `Single`, removing the no-pin `return null`) and 200 (`&&` for `||`). Each either throws inside the `try`, which returns null, or reaches a grant-id comparison that cannot match.
- Reader 43, the block removal of the refused-roster catch. Stryker substitutes `return default`, the same null.
- Rekey 45, removing `checked`. This differs only at `long.MaxValue`, where it wraps instead of throwing.
- Operator 34, the two `??` mutants that read `PartyOf` from admissions first. The admission log is keyed by party and holds every live member's key, so the result is the same live party or an ejected party with the same key, which the gate refuses.
- Pre-insert 972 (competing-root count) and 985:36 (conditional true). For an anchorless tenant the first accepted record is always its genesis, and two competing roots are refused by the chain's multi-genesis rule either way.
- Pre-insert 991:30 and 1041. The relaxation loop reaches the same fixpoint, and `DistinctBy` over the roots is order-free.
- Pre-insert 1057:52 `<= 0` (widens only to an equal signature, which two distinct records cannot share) and 1094:63 (`Guid.ToString("")` is `"D"`).
- Pre-insert 1064 conditional false (an admission judged against `members:revoke`). The rebuilt chain applies `members:admit` itself, so line 1072 refuses the record. `A_member_holding_revoke_but_not_admit_cannot_store_an_admission` passes under a manual mutation of this shape.

**Fail-closed.** These refuse more, throw, or report under another code; none stores a refused record.

- Pre-insert 986 (no relaxation pass) and 1019:54 (`>= 0` refuses every record carrying an unmapped-field map).
- Pre-insert 1055:25 `<=`, 1056:21 `||`, 1056:80 `<= 0`, and 1057:25 `||` widen the preceding revocations.
- Pre-insert 1013, where the malformed-record check is weakened. A blank admitter is refused at line 1065. A blank party makes `RosterSigning.VerifyAdmission` throw, so the fold fails.
- Resolver 105 (`ThrowIfNull` removed) and 178 (NoCoverage, the cancellation rethrow removed, so the resolver returns null instead).

**Diagnostic or audit text only.**

- Resolver 153:48 (the refusal-audit write) and 184-187 (log call and text).
- Pre-insert 995-998 and 1001 (refusal report text and permission label), and 1014, 1016, 1023, 1026, 1037 and 1045 (a refusal code replaced by another non-null string, which still refuses).

**Not dispositioned here.**

- **Ordering (pre-insert 965).** Reversing the pending order changes which of two admissions of one new party, both signed by an eligible admitter, is stored first. That is a convergence question, not an authority gain, and it is left to the roster CRDT convergence tests.
- **Pre-insert 1013 `A && B` for an unparseable `TeamId`.** A record whose team id is not a GUID is verified against `Guid.Empty`. What this run leaves unexamined is whether a self-rooted, self-attested record under such an id could be stored. Any such row sits under an id no GUID-keyed roster reader addresses. This is recorded for follow-up, not claimed equivalent.
- **Gate survivors outside the ejection path.** These are request-shape validation (100, 256-306, 327-336), the prospective-Administrator entry check (83-84), and trace-string and ordering mutants. They are not roster decisions, and a follow-up is proposed for the validation removal.
- **Pre-insert NoCoverage 1081-1086** (`LocalAttestingParty`) belong to the local publish path, not pre-insert verification.

## Tests run

- Affected classes: `NodeAuthorizationRosterConstraintReaderTests` (6), `NodeOperatorIdentityTests` (3), `SelectedSessionPepTests` plus `SelectedSessionEffectivePermissionsTests` (28), `RosterPreInsertVerificationTests` (25) and `DesktopActorRekeyTests` (7): all passed.
- Host suite filtered to `FullyQualifiedName~.Authorization.|FullyQualifiedName~.Identity.|FullyQualifiedName~Roster`: 1,614 passed, 18 skipped, 17 failed. The same 17 fail on unmodified `27dbc9c` in the same container, so this change did not cause them: `AuthorizationAdminRouteTests` (14 cases, `SocketException: Address family not supported by protocol`), `NPrincipalAcceptanceE2E` (n = 3 and 5, `no_response`) and `AdministratorRecoveryCommandTests.ConfiguredDataDirectory_OwnsRecoveryIdentityAndKeystore` (exit code 4, expected 8). This run preceded `f69aa5d`, which adds two pre-insert tests; that class passed 25 of 25 afterwards.

## Reports

The twelve raw reports are about 11 MB each. They embed every host test source, so they are kept off the tracked tree (AGENTS.md). They are archived byte for byte on the orphan branch `archive/ck11-roster-mutation-reports-2026-09-29` at commit `df494c5`, under `docs/evidence/mutation/`, with a `SHA256SUMS` file.

| Report | SHA-256 |
|---|---|
| `ck11-roster-reader-baseline-2026-09-29.json` | `f886d662fa431cc746d3e9458ff8fcc56ac4bf2b6e85118ffd5cd3f3f2c01780` |
| `ck11-roster-reader-final-2026-09-29.json` | `dacf6f800acdaaf28286fe8b84778f49aef5f66bcd9a1184bfa659ddab6f0e7e` |
| `ck11-roster-resolver-baseline-2026-09-29.json` | `1b0fae9b0de3db47ef92feb6bc31275cfcaa0a68a428add387a432ed52562427` |
| `ck11-roster-resolver-final-2026-09-29.json` | `9ad1011a3f230f73d5270f7223a3f0fbd8d300e1861e810530094220fa03b8cc` |
| `ck11-roster-gate-baseline-2026-09-29.json` | `a3f3edd3334130133c1b891a26846b789ba2a0e7542b4bf40b98c315e15f3e60` |
| `ck11-roster-gate-final-2026-09-29.json` | `d7aac25efae33c13a4a97dd48269892210870828d527ac1439e91936d7564e87` |
| `ck11-roster-preinsert-baseline-2026-09-29.json` | `a781d66de3b9a20bdc4ee9c88ddeecd35a6bd9899935c76ce6bb6ecb65baf984` |
| `ck11-roster-preinsert-final-2026-09-29.json` | `8a79127cb47330683ce279574b6ece66f51b2f6247c7f0f75f6eb756a21ff579` |
| `ck11-roster-rekey-baseline-2026-09-29.json` | `5682c496eee95e40b0bed57ba3ba7a7f6437dc1eb135d66ff750f996b0cc0c7f` |
| `ck11-roster-rekey-final-2026-09-29.json` | `3c55026965ed8c78bf26571dcef6d314b892173da8ba63da5d67b78279f3e844` |
| `ck11-roster-operator-baseline-2026-09-29.json` | `cd741b09f7b24ec63d30a2853abd8b7d05d607f7c315c22cc7b59f5d655f4b5e` |
| `ck11-roster-operator-final-2026-09-29.json` | `3b048a380e25cc838bfe28922a9f0aea5a5d194463e14ba255e26af250fc1691` |

The pre-insert final report comes from a second run on `f69aa5d`. An intermediate run on `158ae2f` scored 28.02, left 1057 unkilled, and is not archived.
