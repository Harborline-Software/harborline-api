# ck-4 candidate mutation re-run (T-984)

This note re-runs the DES-0029 `kernel-core-ck-4` (Tenancy) mutation evidence that is stale at the release candidate, under the owner's T-984 Q1 ruling: an item carries only when every mutated file and every killing test file is byte-identical at the candidate. No production code changed.

- api candidate: `9076915d476a3096e78f643c92300fff5e11eb15` (origin/main at the start of this lane, the merge of api #285). Every api run below used this exact commit in a detached worktree.
- platform: `0f30804948db81590c1032b88da9e18ad1dea0d6`, the commit `eng/platform-pin.json` names at the candidate. Feed `0.0.0-alpha.0.hd54e30aa7af1`.
- Tools: .NET SDK `11.0.100-rc.1.26425.128`, Stryker.NET `5.0.0`, winbox.
- Raw reports and logs: orphan branch `archive/ck4-candidate-mutation-2026-09-29` (head `0a6789e5945af10a33ff6ea6394c7df99ac2c36e`), with `reports/SHA256SUMS` and a per-mutant `reports/summary.txt`.

## Carried and stale at this candidate

The control record (release-candidate-2026-09-29) checked the 12 ck-4 items at api `675fbc36`: six carried (T-537, api #275) and six stale (T-946 and api #248, plus the platform tenancy floor). The candidate here is later. Between `675fbc36` and `9076915d`, api #286 (T-537 S1, facade retirement) changed `ActiveTeamTenantContext.cs`, `SelectedSessionTenantContext.cs` and `ActiveTeamTenantBindingArchTests.cs` (the `using` moved from `Harborline.Api.Foundation.MultiTenancy` to `Harborline.Foundation.MultiTenancy`). That makes three T-537 items stale as well: M1/M2, M3 and the hand mutations. They are re-run below. The M4, M5 and M6 files are byte-identical from `9147902a` to `9076915d`, so those three items still carry.

| # | Item (source) | Kind | Status | Result at candidate |
|---|---|---|---|---|
| 1 | T-537 M1/M2 `ActiveTeamTenantContext.cs` (api #275) | stryker-scoped | re-run | 3 Killed, 0 Survived, 0 Timeout, 0 NoCoverage: 100% |
| 2 | T-537 M3 `SelectedSessionTenantContext.cs` (api #275) | stryker-scoped | re-run | 3 Killed, 2 Survived, 0 Timeout, 14 NoCoverage: 15.78% |
| 3 | T-537 M4 `WebTenantSelectionAuthority.cs` (api #275) | stryker-scoped | carried | unchanged, report on `archive/ck4-tenancy-mutation-reports-2026-09-29` |
| 4 | T-537 M5 `WebTenantSwitchAuthority.cs` (api #275) | stryker-scoped | carried | unchanged, same archive |
| 5 | T-537 M6 `CallerTenantIdentifierFence.cs` (api #275) | stryker-scoped | carried | unchanged, same archive |
| 6 | T-537 hand mutations M2, M3, switch candidate check (api #275) | manual | re-run | all three red (below) |
| 7 | T-946 reviewed fence run (docs/evidence/T-946-k4-route-fence-2026-09-28.md) | stryker-scoped | re-run | run A: 46 Killed, 7 Survived, 0 Timeout, 0 NoCoverage: 86.79% |
| 8 | T-946 malformed-body follow-up (same doc) | stryker-scoped | re-run | run A (same configuration at the candidate) |
| 9 | T-946 fence `Use` removal (same doc) | manual | re-run | red (below) |
| 10 | api #248 fence file plus the 4 K4 tests | stryker-scoped | re-run | run B: 46 Killed, 7 Survived, 0 Timeout, 0 NoCoverage: 86.79% |
| 11 | api #248 fence `Use` removal under the 13-spelling walk | manual | re-run | red, 4 of 4 (below) |
| 12 | platform tenancy floor (`tooling/stryker-baselines.json`) | stryker-full | re-run | 6 detected, 5 undetected, 11 tested of 15: 54.54% (break 54) |

Items 7 and 8 were one configuration at two source states in T-946. At the candidate the source is one state, so run A serves both.

## Stryker runs

api runs used `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '<glob>' --filter '<filter>'` from a real console. The generated config is the host `stryker-config.json` with `since` disabled, thresholds `{high 80, low 60, break 0}`, the named `mutate` glob and test-case filter, and default per-test coverage. The platform run used `node tooling/stryker.mjs full hlp.foundation.tenancy.tests` (every mutant, `since` disabled).

| Run | Glob / project | Filter | Killed | Survived | Timeout | NoCoverage | CompileError | Report SHA-256 |
|---|---|---|---:|---:|---:|---:|---:|---|
| A (T-946) | `**/Health/CallerTenantIdentifierFence.cs` | `FullyQualifiedName~CallerTenantIdentifierFenceTests` | 46 | 7 | 0 | 0 | 2 | `3895e60b5519ce3dbd6077dba7a10061034add1b6a25224c90d007760cb0d8c7` |
| B (#248) | `**/Health/CallerTenantIdentifierFence.cs` | `FullyQualifiedName~CallerTenantIdentifierFenceTests\|FullyQualifiedName~RouteAudienceGraphTests.Every_production_route_refuses_caller_supplied_tenant_identifiers` | 46 | 7 | 0 | 0 | 2 | `8ec7ec49f330c01ac9ee64c63718589aa35c440c380296ab245fddb82bb5576b` |
| M1/M2 | `**/Data/Financial/ActiveTeamTenantContext.cs` | `Holds=kernel-core-ck-4` | 3 | 0 | 0 | 0 | 1 | `985e3aea2e9336c1f47b10daa534db3f0efd1e2b2d46382810b445ea4c8f7d5b` |
| M3 | `**/Health/WebSession/SelectedSessionTenantContext.cs` | `Holds=kernel-core-ck-4` | 3 | 2 | 0 | 14 | 1 | `3516b875c46891428325a353955b8cb59f3bac10c2795a9432ed49e492785178` |
| platform | `hlp.foundation.tenancy` (`Harborline.Foundation.MultiTenancy.csproj`) | all 13 tests | 6 | 5 | 0 | 0 | 0 | `4f436077f7ea0e9504d4dcbcf7b449ae3ff2dcf4e962b58321dffbedecca9e9b` |

Both fence runs had zero timeouts, so the 12 unattributed timeouts of the original api #248 run did not recur. In run A, `Every_registered_route_refuses_caller_supplied_tenant_ids` killed 43 mutants and `Malformed_body_is_400_before_route_L985` killed 10, including the multipart and JSON `BadHttpRequestException` throws. In run B, the production-route walk `RouteAudienceGraphTests.Every_production_route_refuses_caller_supplied_tenant_identifiers` is among the killers of 27 mutants, so the 13-spelling walk itself kills fence mutants at the candidate. In M1/M2, `NoActiveTeam_TenantIsUnresolved` killed all 3, and `DifferentActiveTeam_YieldsDifferentTenant`, `SwitchingActiveTeam_SwitchesTenant` and `AmbientTenant_IsNeverTheRetiredLocalLiteral` each killed 2. In M3, `SingleContainerCompositionTests.Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts` killed all 3, including the bind-once guard condition at line 40. The platform kills are all `TenancyContractTests`: unresolved context fails closed (lines 17, 19), system and default tenants fail closed (34, 36), and the `entity.TenantId == tenantId` filter (41) is killed by five filter tests.

## Survivors and dispositions

No survivor weakens a K4 guarantee. None lets a request carry a tenant identifier past the fence, binds a wrong tenant, or widens the tenant filter.

- Fence (runs A and B, the same 7): lines 20, 23, 27, 44 and 58 change `ConfigureAwait(false)` to `true`, which has no effect on tested HTTP behavior (equivalent in ASP.NET Core, which has no synchronization context). Lines 49 and 66 empty the `BadHttpRequestException` message text; the 400 status is unchanged and is asserted. These match the T-946, #248 and M6 dispositions.
- M3, line 31 (constructor block removal) and line 39 (`ThrowIfNull(principal)` removal): argument guards. With them removed, a null resolver or principal still fails with a `NullReferenceException` at first use, so the context fails closed. These match the T-537 dispositions.
- M3, 14 NoCoverage at lines 42-104: code that no `Holds=kernel-core-ck-4` test reaches (the permission resolution and `HasPermission` paths, and the bind-once `throw` at line 42). The same 14 were NoCoverage in T-537. Line 42 needs a note: removing the throw would let a second `Bind` rebind the request's principal. No test in the host project calls `Bind` twice, and the only production caller is the per-request middleware at `SharedHostedWebApp.cs:668`, which binds once. So this is an untested defensive guard, not a reachable leak. The guard condition (line 40) is killed.
- Platform, lines 15, 16 and 33 (`ArgumentNullException.ThrowIfNull` removals): with them removed, a null query or context still throws (the second overload's guard, `NullReferenceException` at line 17, or `Queryable.Where`'s own null check), so the filter fails closed. Lines 20 and 37 empty the exception messages; the exception types are asserted. These are the same five survivors behind the 2026-09-26 baseline (54.54%, break 54).

## Manual mutations

Each hand mutation was applied in the candidate worktree, the named tests were run in Debug, and the file was restored with `git checkout`. The unmutated controls passed: the 4 fence tests (4/4) and the 13 tests of the T-537 set (13/13). The diffs and logs are under `manual/` on the archive branch.

| Item | Mutation | Tests run | Result |
|---|---|---|---|
| T-946 and #248 `Use` removal | Both `CallerTenantIdentifierFence.Use(_app);` calls in `SharedHostedWebApp.cs` (lines 259 and 342) commented out | `CallerTenantIdentifierFenceTests` and the production-route walk | 4 of 4 failed. The walk reports `GET /api/local-node/accounting-periods query: expected 400, got 401`; the fence tests expected `BadRequest` |
| T-537 M2 | `ActiveTeamTenantContext.ProjectTenantId` returns a constant tenant (line 60) | `ActiveTeamTenantBindingArchTests` | 3 of 9 failed: same team always projects the same tenant, a different team yields a different tenant, switching the team switches the tenant |
| T-537 M3 | `Id = principal.TenantId` replaced by a fixed tenant (line 49) | `Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts` | 1 of 1 failed |
| T-537 switch | `if (false && candidate is null)` at `WebTenantSwitchAuthority.cs:367`, with `candidate?.DisplayLabel ?? targetTenantId` at :392 so the mutant compiles | `WebTenantSwitchAuthorityTests` | 1 of 3 failed: `Switch_to_a_tenant_outside_the_accounts_candidates_is_refused_before_any_head_is_written` |

## Slice baselines

The `scope-tenancy`, `scope-tenancy-identity-tenant` and `scope-tenancy-identity-session` slices run separately on the `mutation.yml` workflow at the same commit (`9076915d`): run `36644970273` (in progress when this note was written) and run `36645000780` (queued). Run `36644984613` was cancelled. Their scores are not recorded here; the controller reads them from the run.

## Archive

| File | SHA-256 |
|---|---|
| `reports/t946-fence.mutation-report.json` | `3895e60b5519ce3dbd6077dba7a10061034add1b6a25224c90d007760cb0d8c7` |
| `reports/pr248-fence-walk.mutation-report.json` | `8ec7ec49f330c01ac9ee64c63718589aa35c440c380296ab245fddb82bb5576b` |
| `reports/m1m2-active-team.mutation-report.json` | `985e3aea2e9336c1f47b10daa534db3f0efd1e2b2d46382810b445ea4c8f7d5b` |
| `reports/m3-selected-session.mutation-report.json` | `3516b875c46891428325a353955b8cb59f3bac10c2795a9432ed49e492785178` |
| `reports/platform-tenancy.mutation-report.json` | `4f436077f7ea0e9504d4dcbcf7b449ae3ff2dcf4e962b58321dffbedecca9e9b` |

The configs, `summary.txt` and the manual logs are hashed in `reports/SHA256SUMS` on the archive branch.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
