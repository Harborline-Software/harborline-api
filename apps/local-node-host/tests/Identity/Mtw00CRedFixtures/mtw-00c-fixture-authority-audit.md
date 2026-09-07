# MTW-00C fixture authority audit

Audit basis: the 18 entries in `Mtw00CRedFixtureCatalog` on `origin/main` at the start of
card 3247. “Production proof” means the fixture body executes the named production authority and
contains an assertion that fails when that authority is removed or subverted. “Pending” means the
authority is not present on main and the discovery fixture still terminates in
`MissingAuthorityException`.

| Fixture | State | Production authority exercised |
|---|---|---|
| `cookie-audience-separation/selected_extractor_rejects_challenge_audience_handle` | Production proof | `SharedHostedWebApp.ClassifyWebCookieAudience` |
| `cookie-audience-separation/no_audience_fallback_after_miss` | Production proof | `SharedHostedWebApp.ClassifyWebCookieAudience` |
| `cookie-audience-separation/no_audience_fallback_after_refusal` | Production proof | `SharedHostedWebApp.ClassifyWebCookieAudience` |
| `cookie-audience-separation/installation_audience_cannot_authorize_business_route` | Production proof | `SharedHostedWebApp.ClassifyWebCookieAudience` |
| `r3h-coordinated-transition/select_success_invisible_before_all_audit_heads_completed` | Production proof | `WebTenantSelectionAuthority` |
| `r3h-coordinated-transition/switch_revokes_old_session_only_after_both_tenant_audits_completed` | Production proof | `WebTenantSwitchAuthority` |
| `r3h-coordinated-transition/logout_success_waits_for_completed_and_stale_handle_fails_after_commit` | Production proof | `WebSelectedSessionLogoutAuthority` |
| `legacy-v1-cutover/installation_migration_lease_is_exclusive_and_restart_safe` | Production proof | `InstallationIdentityCutoverOrchestrator.AcquireLeaseAsync` over reopened SQLite |
| `legacy-v1-cutover/v1_mutation_write_barrier_blocks_new_mutation_while_lease_held` | Production proof | `InstallationIdentityCutoverOrchestrator.CheckV1MutationAdmissionAsync` and `CheckLegacyBearerAdmissionAsync` |
| `legacy-v1-cutover/v2_authority_marker_cas_flips_exactly_once` | Production proof | `InstallationIdentityCutoverOrchestrator.AdvanceAsync` under a 100-way race |
| `legacy-v1-cutover/post_cutover_v1_bearer_is_rejected` | Production proof | `InstallationIdentityCutoverOrchestrator.CheckLegacyBearerAdmissionAsync` after the committed marker |
| `credential-recovery/recovery_challenge_is_purpose_bound` | Production proof | `AccountSetupInvitationStore` and `RecoveryInvitationStore` |
| `credential-recovery/recovery_code_consumes_exactly_once_across_restart` | Production proof | `RecoveryInvitationStore` over reopened SQLite |
| `credential-recovery/credential_recovery_revokes_all_audiences_before_success` | Production proof | `AccountCredentialRecoveryService` and `RecoverySessionRevoker` |
| `capability-side-door/operator_flag_cannot_enable_unadmitted_capability` | Production proof | `RuntimeCapabilityDisableFence`; non-escalation asserted on Compiled, Admitted and Ready |
| `capability-side-door/route_registration_or_di_presence_cannot_enable_capability` | Production proof | `RuntimeCapabilityCatalog.SnapshotAsync` over a real registration set |
| `capability-side-door/pack_install_or_per_tenant_activation_cannot_enable_capability` | Production proof | `RuntimeCapabilityCatalog` constructor surface, public and internal |
| `capability-side-door/global_disable_is_veto_only_never_enable` | Production proof | `RuntimeCapabilityDisableFence` with both vetoes engaged |

The catalog no longer has a string registration set. A pending fixture can become a production
fixture only by replacing `Red.Fixture` with `Production.Fixture` and supplying an executable proof.
The always-on meta-test runs every production proof, while the opt-in red suite continues to expose
any genuinely pending authorities.

## Card 3240 update — the four `legacy-v1-cutover` entries

`InstallationIdentityCutoverOrchestrator` now ships the migration lease, the durable v1 write
barrier, the marker CAS, and the post-CAS bearer refusal, so those four entries moved from pending to
production proof. Their bodies live in `LegacyV1CutoverAuthorityProof` and drive the real
orchestrator over file-backed SQLite across a genuine close/reopen, with the ADMITTING state asserted
before each refusal so a gate that always refuses cannot pass them.

The catalog now has zero pending entries. `CatalogEntry_Resolves_ThroughItsDeclaredState` therefore
enumerates every entry and branches on its state rather than filtering to pending — under xUnit 2 a
`[MemberData]` theory with no rows fails, which would have turned the good outcome red.

### Reverse-red verification of the four cutover proofs

Run against the committed merge, one mutation at a time, each restored with
`git checkout HEAD --` before the next. Every row asserted the mutation was present in the file and
grepped the run for `error CS` alongside the pass/fail counts, so a compile break could not read as
silence.

| Mutation to `InstallationIdentityCutoverOrchestrator` | Outcome |
|---|---|
| `LeaseIsHeld` no longer compares the supplied `LeaseId` | lease proof failed — "An altered lease capability was accepted" |
| `CheckV1MutationAdmissionAsync` drops the cutover-stage clause | barrier proof failed — "A v1 mutation was admitted while the write barrier was durably raised" |
| `CheckLegacyBearerAdmissionAsync` drops the cutover-stage clause | barrier proof failed — "Legacy bearer audience 'AccountChallenge' was admitted behind a raised barrier" |
| the marker CAS no longer requires staged bearer-revocation evidence | CAS proof failed — the premature-CAS assertion |
| the post-cutover refusal always reports migration-in-progress | rejection proof failed — the retirement-code assertion |

One further mutation was tried and NOT caught, and it is recorded here rather than papered over:
dropping `state.V1WriteBarrierVersion == 0` from `CheckV1MutationAdmissionAsync` left the whole
suite green. That clause is an equivalent mutant, not a coverage gap. `V1WriteBarrierVersion` is
incremented only on the transition into `WriteBarrierActive`, the stage machine never returns to
`LegacyV1Authoritative`, and `ResolveAuthorityAsync` already refuses to call a
`LegacyV1Authoritative` state with a raised barrier readable — so no reachable state distinguishes
the two conditions. The clause is defence in depth and should stay; a test that appeared to cover it
would be asserting something the public API cannot reach.

## Card 3245 update — `post_cutover_v1_bearer_is_rejected` now drives production accept paths

The deep review of PR 3245 found this fixture could not detect the property it claimed. Its body
looped the four `InstallationIdentityLegacyBearerAudience` values against
`CheckLegacyBearerAdmissionAsync`, which is audience-INSENSITIVE — it reads one durable stage and
ignores the audience — so the loop re-ran a single stage check four times. It proved the gate
refuses; it could not prove any production path ASKS the gate. Two named accept paths were in fact
unwired and stayed fully reachable after the marker committed while the fixture stayed green:

- `WebAccountAccessChallengeIssuer.IssueAsync` — the ISSUE half of the `AccountChallenge` audience.
- `WebTenantSelectionAuthority.SelectAsync` — the CONSUME half; an already-issued challenge stayed
  spendable after the marker, minting a fresh selected session from retired v1 authority.

Both are now gated, and the fixture body was split: `ProveOrchestratorRefusesEveryAudienceAfterTheMarker`
keeps the original gate-level assertions, and `ProveProductionAcceptPathsRefuseAfterTheMarker` drives
four real authorities over their own file-backed stores. Each asserts the ADMITTING state, then
commits a real v2 marker through `CutoverAdvance.CommitV2MarkerAsync` and asserts the same call is
refused — so the admit-before-refuse discipline the rest of this file follows is preserved.

`NodeWebSessionAuthority.LogoutAsync` is deliberately NOT gated (the same review found the gate there
left live v1 records behind a 204 while `SessionLogoutRoutes.SelectedLogoutAsync` answered success
anyway). Revocation grants nothing and is what the cutover exists to accomplish, so a regression
guard for the ASYMMETRY is included rather than a gate.

### Reverse-red verification of the extended proof

One mutation at a time, each restored before the next. Every row confirmed the mutation was present
(`grep -c CheckLegacyBearerAdmissionAsync` on the mutated file) and that the build still succeeded,
so a compile break could not read as a caught mutant.

| Mutation | Outcome |
|---|---|
| `WebTenantSelectionAuthority.SelectAsync` drops its gate call | `Select_Is_Refused_After_The_Real_V2_Marker_Commits` failed at `WebTenantSelectionAuthorityTests.cs:232` — `Assert.Null` on the post-marker `SelectAsync`, actual a fully minted `WebTenantSelectionResult` |
| `WebAccountAccessChallengeIssuer.IssueAsync` drops its gate call | `Challenge_Issue_Is_Refused_After_The_Real_V2_Marker_Commits` failed at `WebAccountAccessChallengeIssuerTests.cs:447` — `Assert.Null` on the post-marker `IssueAsync`, actual a fully minted `WebAccountAccessChallengeIssueResult` |
| `NodeWebSessionAuthority.LogoutAsync` re-gates revocation (the #3245 F1 defect, reinstated) | `PostCutoverLogout_PresentingBothAudiences_RevokesTheLegacyRecord` failed at `NodeWebSessionAuthorityTests.cs:827` — `Assert.Null` on the store lookup, actual the surviving `SessionRecord`, while the route still returned 204 |

Each mutation also turned the three MTW-00C meta-tests red through
`post_cutover_v1_bearer_is_rejected`, which is the fixture-level signal that matters:
`CatalogEntry_Resolves_ThroughItsDeclaredState`, the restart-shaped fixture test, and
`every claimed-built authority passes only through its production proof`.

## Reverse-red verification

The production-proof meta-test was rebuilt and run against three temporary mutation groups, then
the mutations were removed:

1. `SharedHostedWebApp.ClassifyWebCookieAudience` classified selected and foreign cookies as
   `LegacyEligible`: all four cookie fixtures failed.
2. `WebTenantSelectionAuthority`, `WebTenantSwitchAuthority`, and
   `WebSelectedSessionLogoutAuthority` were prevented from persisting `Completed`: the three R3-H
   fixtures failed.
3. The account-setup purpose predicate, completed-recovery replay fence, and recovery-revocation
   commit were removed: the three recovery fixtures failed.

That covers every fixture claimed as a production proof. The restored production code and the
complete MTW-00C fixture gate pass green.
