# T-537 K4 tenancy mutation evidence

This note records the DES-0029 `kernel-core-ck-4` mutation evidence for T-537 slice S2. The source is api commit `b4fe9efdf90b9b9201d67fbe67bb48548cb5388e` plus this change's test-only diff. The platform pin is `cbc84d5934314098b8f6eff3de94cfa5de3ea8f3` (`eng/platform-pin.json`, feed `0.0.0-alpha.0.h333652cee866`). The tools were .NET SDK `11.0.100-rc.1.26425.128` and Stryker.NET `5.0.0` on winbox. No production code changed.

## Tests

The ADR-0103 id is carried by `[Trait("Holds", "kernel-core-ck-4")]`. No test was renamed or removed. The tagged set is:

- `ActiveTeamTenantBindingArchTests`: `DifferentActiveTeam_YieldsDifferentTenant`, `SwitchingActiveTeam_SwitchesTenant`, `AmbientTenant_IsNeverTheRetiredLocalLiteral`, `NoActiveTeam_TenantIsUnresolved`, `SameTeam_ProjectsToSameTenant_Deterministically`
- `SingleContainerCompositionTests.Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts`
- `RouteAudienceGraphTests.Every_production_route_refuses_caller_supplied_tenant_identifiers`, `CallerTenantIdentifierFenceTests.Every_registered_route_refuses_caller_supplied_tenant_ids` (the latter was already tagged)
- `WebTenantSelectionAuthorityTests.Invalid_Explicit_Tenant_Refuses_Without_Consuming_Challenge` (two cases), `Unknown_And_Unusable_Tenants_Have_Equivalent_External_Refusals_And_Documented_Read_Profiles`
- New: `WebTenantSwitchAuthorityTests.Switch_to_a_tenant_outside_the_accounts_candidates_is_refused_before_any_head_is_written`

The new test covers the switch side of the owner's 2026-09-28 select/switch exception. The fixture's candidate locator leaves out the target tenant, while the target partition would still report a usable membership, so only the candidate check can refuse the switch. The test asserts a null result, no tenant finalization, no coordinator row, both original sessions and no revocation, and that the old handle stays active. It passed on the unchanged source. To prove it red, the check at `WebTenantSwitchAuthority.cs:367` was disabled by hand (`if (false && candidate is null)`, with `candidate?.DisplayLabel ?? targetTenantId` at :392 so the mutant compiles). The new test then failed, 1 of the class's 3. The edit was reverted.

The other two switch tests (`Switch_Completes_Both_Tenant_Heads_Before_Atomic_Rotation`, `Interrupted_Tenant_Finalization_...`) are left untagged. They prove atomic session rotation and recovery, not the K4 tenant choice.

## Configuration

Each file had its own scoped run: `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '<glob>' --filter 'Holds=kernel-core-ck-4'`. The script's generated config is the host `stryker-config.json` (`project: Harborline.LocalNodeHost.csproj`, json and progress reporters) with `since` disabled, thresholds `{high 80, low 60, break 0}`, `mutate: ["<glob>"]`, `test-case-filter: "Holds=kernel-core-ck-4"`, and the default per-test coverage analysis. The raw reports are not tracked; the controller archives them.

| Run | Glob | Killed | Survived | Timeout | NoCoverage | Report SHA-256 |
|---|---|---|---|---|---|---|
| M1/M2 | `**/Data/Financial/ActiveTeamTenantContext.cs` | 3 | 0 | 0 | 0 | `14bd9c5da9aa0ad8444994a0f1d4f59a755ad7d57150adf03670407af06e0f7e` |
| M3 | `**/Health/WebSession/SelectedSessionTenantContext.cs` | 3 | 2 | 0 | 14 | `c1fa122b2ed83749afd20ed6d9fafd0b562133014c8da1abdbf67a9d217be7f8` |
| M4 | `**/Data/Identity/WebTenantSelectionAuthority.cs` | 20 | 14 | 2 | 228 | `e8842026ee39e9b1a0af0383379ad319a360964e16e17e7b8ac1ca9e5ce03b30` |
| M5 | `**/Data/Identity/WebTenantSwitchAuthority.cs` | 8 | 27 | 1 | 343 | `27cb88f9888e7518465948626919aba1836d163cc4cc3f70f2e959ca87363ed3` |
| M6 | `**/Health/CallerTenantIdentifierFence.cs` | 44 | 5 | 0 | 4 | `a39d7e4bb3ff4ad17668bacd1ef81504a19c802e5d4fd3169cb9431700c8b2e8` |

These are scoped runs of one file each under the K4 filter, not slice or whole-host scores. NoCoverage counts code that no K4-tagged test reaches, such as the rest of the select and switch protocol and `HasPermission`.

## Named mutants and their killers

| # | Site | Mutant | Status | Killing test |
|---|---|---|---|---|
| M1 | `ActiveTeamTenantContext.cs:69-73` | 5224 block removal of the no-active-team `return null` | Killed | `NoActiveTeam_TenantIsUnresolved` |
| M1 | `ActiveTeamTenantContext.cs:68` | 5223 `active is not null` | Killed | `DifferentActiveTeam_YieldsDifferentTenant`, `NoActiveTeam_TenantIsUnresolved`, `SwitchingActiveTeam_SwitchesTenant`, `AmbientTenant_IsNeverTheRetiredLocalLiteral` |
| M2 | `ActiveTeamTenantContext.cs:60` | `ProjectTenantId` returns a constant tenant (hand mutation; Stryker generates no mutant for this expression) | Killed | `DifferentActiveTeam_YieldsDifferentTenant`, `SwitchingActiveTeam_SwitchesTenant`, `SameTeam_ProjectsToSameTenant_Deterministically` |
| M3 | `SelectedSessionTenantContext.cs:49` | `Id = principal.TenantId` changed to a fixed tenant (hand mutation; Stryker generates no mutant for the initializer) | Killed | `Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts` (1 of 12 K4 cases failed) |
| M3 | `SelectedSessionTenantContext.cs:40` | 43168 bind-once guard `_principal is null` | Killed | `Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts` |
| M4 | `WebTenantSelectionAuthority.cs:388` | 15552 un-negate the candidate check, 15553 `Any` to `All`, 15554 `==` to `!=` | Killed | `Unknown_And_Unusable_Tenants_Have_Equivalent_External_Refusals_And_Documented_Read_Profiles` |
| M4 | `WebTenantSelectionAuthority.cs:389-391` | 15555 block removal of the not-a-candidate `return null` | CompileError (nullable flow) | none; the three :388 kills cover the check's condition |
| M5 | `WebTenantSwitchAuthority.cs:368-370` | 15845 block removal of `candidate is null` return | Killed | `Switch_to_a_tenant_outside_the_accounts_candidates_is_refused_before_any_head_is_written` |
| M5 | `WebTenantSwitchAuthority.cs:365-367` | 15842 `SingleOrDefault` to `Single`, 15843 `!=`, 15844 `candidate is not null` | Killed | `Switch_to_a_tenant_outside_the_accounts_candidates_is_refused_before_any_head_is_written` |
| M6 | `CallerTenantIdentifierFence.cs` | 44 mutants over query, header, route, form, multipart and JSON detection and the select/switch root exception | Killed | Each report entry names `CallerTenantIdentifierFenceTests.Every_registered_route_refuses_caller_supplied_tenant_ids` and/or `RouteAudienceGraphTests.Every_production_route_refuses_caller_supplied_tenant_identifiers` |

The M6 rerun had zero timeouts, so the 12 unattributed timeouts in the PR #248 run did not recur under the K4 filter.

## Survivors

None of the survivors weakens a K4 guarantee:

- ConfigureAwait: every Boolean `true` survivor is `.ConfigureAwait(false)` changed to `true`. That is M6 lines 20, 23, 27, 44 and 58, M4 lines 110, 119, 123, 133, 354, 360, 371 and 399, and M5 lines 93, 101, 109, 118, 364, 423, 428, 434, 442 and 445.
- Equivalent: `Guid.ToString("D")` changed to `ToString("")` gives the same format. This is M4 line 387 and M5 line 91.
- Argument guards: the M3 constructor null-guard block (line 31) and `ThrowIfNull(principal)` (line 39), plus the M4 and M5 `_sessionOptions.Validate()` statement removals (lines 83 and 77).
- Session and challenge authentication checks, which the K4 filter reaches only on the refusal path: M4 line 126 (the challenge consumed/revoked combinations) and 357 (the account query), and M5 lines 86, 94, 105, 425-429 and 444 (the handle, old session, current session, account and correlation checks). These belong to the session-authority tests and to the nightly `scope-tenancy-identity-tenant` and `scope-tenancy-identity-session` slices, not to K4.

## Slice baselines

`eng/baselines/mutation-baseline.json` slices `scope-tenancy` and `scope-tenancy-identity-tenant` stay `{"status": "pending"}`. Their schema requires `{score, break, measured, run}`, where `run` is the Actions run id of a `mutation.yml` nightly slice run, and these scoped local runs are not slice runs.

## Host suite

The full `apps/local-node-host/tests/tests.csproj` run on this source passed 4,543 tests. It skipped 22 and failed 1, for 4,566 in total, in 7 minutes 13 seconds. Its local TRX is `apps/local-node-host/tests/TestResults/ck4s2-full.trx` (SHA-256 `0b1690cb3973a232088c320bec639d4895c91e45773158ac845dcca6b3a19be1`). The one failure was `HarborlineXliffTargetTests.Export_and_import_targets_execute_against_a_real_localized_project` (MSB4062). It needs the local Release build of `tooling/Harborline.Tooling.LocalizationXliff`, which this checkout lacked, as the T-946 note also records. After that build, the xliff test and every ArchTests test passed: 274 of 274. The host baselines need no edit. `knownTests` is written only by `eng/run-exact-clone.mjs --write-known-tests`, and an added identity needs no declaration (T-724 ruling 119). No test was renamed or removed, so no `policyRemovals` row is needed.
