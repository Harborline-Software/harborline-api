# T-975 asset-registry write gates, scoped mutation evidence

The public seam is the production `AssetRegistryRoutes.Map` handler (`apps/local-node-host/Health/AssetRegistryRoutes.cs`) behind an in-process HTTP listener. The risk is a denied caller reaching a type, entity or edge write. Every write gate was first rewritten from `if (await RequestAuthorization.RefusalAsync(...) is { } denied) return denied;` to `var denied = await RequestAuthorization.RefusalAsync(...); if (denied is not null) return denied;`. This is behaviour-neutral, and it applies to all 60 host call sites of `RequestAuthorization.RefusalAsync` (the pattern-variable form was rewritten at 57; three already used the explicit form). The rewrite was needed because Stryker.NET 5.0.0 rolls back the negation of the pattern form as a CompileError (`denied` becomes unassigned) and makes no block-removal mutant for a one-statement `if`, so it could not produce a bypass mutant. The precedent is API `7dce725c`, which made the same kind of change for `SubmitValidationGate`.

The run used repository-pinned Stryker.NET 5.0.0 through `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Health/AssetRegistryRoutes.cs' --filter 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.AssetRegistry.'`. It used source project `Harborline.LocalNodeHost.csproj`, per-test coverage and the pinned SDK's `MSBuild.exe`, and took 762 s. For the file, 232 mutants were Killed, 126 Survived, 42 NoCoverage and 10 Timeout, with 80 CompileError and 56 Ignored. The score is 59.02%. Stryker's project-wide safe mode rolled back 5,739 host mutants as CompileError; they are not evidence either way.

## Named bypass kills

Each row is the negated guard (`denied is not null` to `denied is null`), which lets a denied caller through and refuses an allowed one.

| Write shape | Guard line | Mutant | Killing denial test |
| --- | ---: | --- | --- |
| `POST /types` (create, `packages:author` on `asset-type`) | 163 | 31455 Killed (8/8 covering tests) | `AssetRegistryRouteTests.Types_Create_DeniedPackageAuthorCannotPersist` |
| `PUT /types/{id}` (edit or override) | 208 | 31485 Killed (14/15) | `AssetRegistryRouteTests.Types_Update_DeniedPackageAuthorCannotPersist` |
| `POST /types/{id}/revert` | 290 | 31536 Killed (2/2) | `AssetRegistryRouteTests.Types_Revert_DeniedPackageAuthorCannotPersist` |
| `POST /entities` with a client id (the decision precedes the id refusal) | 392 | 31621 Killed (2/2) | `AssetRegistryRouteTests.Entities_Create_DenialPrecedesClientSuppliedRecordIdRefusal` |
| `POST /entities`, unbound branch (`records:write`) | 468 | 31658 Killed (31/31) | `AssetRegistryRouteTests.Entities_Create_UnboundDeniedWriteCannotPersist` |
| `POST /edges` (`records:write`) | 570 | 31707 Killed (4/4) | `AssetRegistryRouteTests.Edge_Create_DeniedWriteCannotPersist` |

In each of these rows, the permission-string mutant on the gate call (for example `Permission.PackagesAuthor` becoming `""`) is also Killed. The bound `POST /entities` branch decides inside `NodeEntityWriter`, not in this file. Its denial proof is `PackBoundSelectedPrincipalTests.A_grant_to_the_attribution_party_cannot_authorize_the_selected_principal`.

## Survivors on the write handlers, classified

The write handlers are lines 150-316, 380-490 and 562-603. They have 52 Survived and 14 NoCoverage mutants. None of them bypasses a gate.

- **Tenant selection (a real gap, now closed).** The four write handlers pick `SelectedSessionRequestPrincipal.TenantId ?? NodeTenant.Resolve(activeTeam)`. The remove-left mutants on lines 152, 201, 283 and 564 survived, because the route tests never set a selected principal whose tenant differs from the active team. That is a tenant-of-decision fault, not a bypass. Two new tests close it. Both open the real route with a selected principal in tenant A and an active team in tenant B:
  - `PackBoundSelectedPrincipalTests.Type_writes_decide_in_the_selected_tenant_when_the_active_team_differs` covers type create, edit and revert.
  - `PackBoundSelectedPrincipalTests.Edge_write_decides_in_the_selected_tenant_when_the_active_team_differs` covers the edge write.
  - Each mutant was applied by hand and the test run, and each made the new test fail (for line 283 the failure was `Expected: OK, Actual: Forbidden`, the route refusing an allowed caller because it decided in the ungranted tenant). The source was then restored. Mutant 152 was killed by the type test, 201 by the type test, 283 by the type test and 564 by the edge test.
  - The entity-create equivalent (line 382) was already Killed by `PackBoundSelectedPrincipalTests.Canonical_principal_grant_authorizes_while_party_remains_record_author`.
- **Equivalent.**
  - `ConfigureAwait(false)` to `true` on the handlers' awaited calls. There is no synchronization context under ASP.NET Core.
- **Response shape, not authorization.** These mutants change a response body or wire field, not a decision:
  - error-code strings (lines 166, 175, 177, 193, 211, 273, 307, 385, 394, 401, 455, 573, 575, 577 and 601)
  - `Results.Created` location strings (lines 187, 429, 482 and 592)
  - wire booleans `overridesSeed`, `seedExists` and `hasTenantRow` (lines 189, 247, 265 and 302)
  - `EffectiveFrom` formatting (line 594)
  - `ScanKey` trimming (lines 423 and 477)
- **Input validation reachable only after the gate allows.** These are follow-ups for the route owner, not T-975:
  - the `||` to `&&` validation mutants on lines 165, 210, 384 and 574
  - the `body.Id` fallback on line 156
  - the type-existence check on line 400
  - the ticket-155 loss-disposition boundary `losses.Count > 0` to `>= 0` on line 234

## Guard rewrite verification

`dotnet build` gave 0 warnings and 0 errors. The focused set passed 387/387 (AssetRegistryRouteTests, PackBoundSelectedPrincipalTests, AuthorizationGateTests, RosterGateDecisionTests and the ArchTests namespace).

The first full host run after the rewrite had 6 failures:
- the four `layout-eng-31` timing-gate tests
- `T-735: DrainAsync stops waiting ...`
- `HarborlineXliffTargetTests.Export_and_import_targets_execute_against_a_real_localized_project`, which fails with MSB4062 because the localization task assembly cannot load in a worktree

The same filter without the rewrite (`git checkout` of `apps/`, rebuild) failed 3 of the same 21 tests, so these are environmental timing and tooling failures, not rewrite regressions. With every change of this slice, the full host project passed 4,549 with 22 skipped and 4 failed. The four failures were the `layout-eng-31` timing gates (three tests) and the XLIFF MSB4062 test. The `layout-eng-31` filter alone fails 3 of 16 on this loaded host without the rewrite as well.

## Raw reports

The raw Stryker.NET JSON reports are not kept in the tracked tree. Each is about 12 MB and embeds every host test source, which the retired-prefix scan, the identity-r3 era-token scan and the quality gate's input-size limit all reject.

| Report | SHA-256 |
| --- | --- |
| The namespace-filter report cited above | `D8EAB2F34892ABC1127AF97CA73612A79412DC7657C8775D9F359424910F87D7` |
| The earlier route-class-filter report | `73D68E32EFF41804CD6F6E7758B0A96D6B237228470F443953903552144CF664` |

The route-class-filter report used `FullyQualifiedName~AssetRegistryRouteTests` and took 659 s: 219 Killed, 143 Survived, 48 NoCoverage and 80 CompileError, with the same six guard kills. Archive both reports on an `archive/*` branch before citing them in DES-0029.

This is scoped mutation evidence for the asset-registry write gates, not a certificate for every API write route. It does not close T-519's gate and pipeline obligations.
