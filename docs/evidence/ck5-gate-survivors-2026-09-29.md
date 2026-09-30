# ck-5 AuthorizationGate survivors, killed or dispositioned

This note records DES-0029 `kernel-core-ck-5` (Authorization) mutation evidence for `packages/foundation-authorization/AuthorizationGate.cs`. API PR #269 scored the file at 79.41% and listed its survivors, but #269 is not merged. This change re-measures the file on current main, kills every silent-failure survivor with a behaviour-named test, and gives every other survivor a reason.

The source is api main `27dbc9c` plus this change's test-only diff. The platform pin is `a23afe320807e7c7775585a6246ca9981b337bb7` (`eng/platform-pin.json`). The tools were .NET SDK `11.0.100-rc.1.26425.128` and Stryker.NET `5.0.0` (repository tool manifest) on a 4-core Linux container. No production code changed.

## Run

Both runs used the same command, with the test filter #269 used:

```
node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj \
  --scoped '**/AuthorizationGate.cs' --project Harborline.Foundation.Authorization.csproj \
  --filter 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Authorization.|FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Identity.'
```

| | Before (main `27dbc9c`) | After (this change) |
| --- | ---: | ---: |
| Tests selected | 1,418 | 1,433 |
| Killed | 162 | 183 |
| Survived | 26 | 17 |
| NoCoverage | 16 | 4 |
| CompileError (in file) / Ignored | 6 / 22 | 6 / 22 |
| **Score** | **79.41%** | **89.71%** |
| Duration | 4,071 s | 7,507 s |

The before score equals #269's. The 73 project-wide CompileErrors are Stryker's rollbacks (AGENTS.md "Mutation testing"), unchanged between runs.

## Tests

`apps/local-node-host/tests/Authorization/AuthorizationGateTests.cs`:
- #269's three methods are carried word for word, so the two branches merge cleanly: `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead`, `DecideAsync_DecidesATenantTargetAtTheInstallRoot` and `Dedicated_entry_points_refuse_an_act_that_is_not_their_own`.
- Six methods are new:
  - `Catalogue_field_target_that_disagrees_with_its_field_is_refused_before_closure_read`
  - `DecideAsync_RefusesARecordIdWithoutARecordKind`
  - `Dedicated_entry_points_refuse_a_null_request_as_an_argument_error`
  - `Unverified_roster_fallback_is_recorded_in_the_decision_evidence`
  - `Resolution_trace_describes_each_binding_and_standing`
  - `Attenuation_evidence_is_ordered_whatever_the_reader_order`

The tests fence behaviour that already exists, so their red is the mutant (T-724 ruling 35). Each mutant below was applied by hand, the gate test class was run, and the source was restored. Every one turned the named test red, and every test passed without it. The statement removals used `{ }` for the removed `throw`, because a bare `;` after an unbraced `if` is a build error here.

## Killed (before Survived or NoCoverage, after Killed)

The "Killing test" column is the report's `killedBy`, which agrees with the manual red check.

| Line | Mutant | Silent failure? | Killing test |
| ---: | --- | --- | --- |
| 300 | catalogue field check `\|\|` to `&&` | **Yes.** A catalogue target whose record id, or whose act, disagrees with the field it names passes validation, and a grant on the source record then allows it. | `Catalogue_field_target_that_disagrees_with_its_field_is_refused_before_closure_read` (both cases) |
| 301 | catalogue disagreement `throw` removed (was NoCoverage) | **Yes**, as 300 | same |
| 84 (x2) | prospective-Administrator act check `\|\|` to `&&` (outer and inner) | **Yes.** The Administrator atoms are added for an act that is not the members handover. | `Dedicated_entry_points_refuse_an_act_that_is_not_their_own` (#269) |
| 87 | prospective entry `throw` removed (was NoCoverage) | **Yes**, as 84 | same |
| 64 | membership-admission act check `\|\|` to `&&` | **Yes.** The entry point accepts an act that is not its own. | same |
| 67 | membership entry `throw` removed (was NoCoverage) | **Yes**, as 64 | same |
| 275 | install-wide root-scope check `\|\|` to `&&` | **Yes.** An install-wide act is decided off the install root, so a narrow grant confers it. | `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead` (#269) |
| 277 | install-wide scope `throw` removed (was NoCoverage) | **Yes**, as 275 | same |
| 330 | tenant-id check un-negated (was NoCoverage) | **Yes.** A tenant target naming another tenant is decided. | `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead`, `DecideAsync_DecidesATenantTargetAtTheInstallRoot` (#269) |
| 331 | other-tenant `throw` removed (was NoCoverage) | **Yes**, as 330 | `DecideAsync_RefusesAnInvalidTargetBeforeSnapshotRead` (#269) |
| 336 | scope-separator `throw` removed | **Yes.** A record id `a/b` is decided as the nested scope `/records/a/b`. | same |
| 265 | `IsNullOrWhiteSpace(RecordId)` to `(RecordId != null)` | **Yes.** A target that has a record id but no record kind is read as install-wide. | `DecideAsync_RefusesARecordIdWithoutARecordKind` |
| 332 | tenant target scope `"/"` to `""` (was NoCoverage) | No; it refuses a valid tenant target | `DecideAsync_DecidesATenantTargetAtTheInstallRoot` (#269) |
| 63, 83 | `ThrowIfNull(request)` removed in each dedicated entry point | No; a `NullReferenceException` still fails closed | `Dedicated_entry_points_refuse_a_null_request_as_an_argument_error` |
| 107 | refuse-fallback object initializer dropped (`RegistryMember` null) | No: evidence. The `registry:member` line disappears. | `Unverified_roster_fallback_is_recorded_in_the_decision_evidence` |
| 108 | refuse-fallback `RegistryMember = false` to `true` | No: evidence. The fallback would claim registry membership. | same |
| 227 | attenuation binding order `OrderBy` to `OrderByDescending` | No: evidence order | `Attenuation_evidence_is_ordered_whatever_the_reader_order` |
| 228 (x2) | attenuation exclusion order `OrderBy` and `ThenBy` to descending | No: evidence order | same |
| 382 | derivation description emptied | No: evidence text | `Attenuation_evidence_is_ordered_whatever_the_reader_order` (report); the manual red check also fails `Resolution_trace_describes_each_binding_and_standing` |
| 385 | standing description emptied | No: evidence text | `Resolution_trace_describes_each_binding_and_standing` |

The ck-5 decision-evidence branches in this file are the refuse-fallback roster facts (107-108), the resolution-trace descriptions (382, 385) and the attenuation evidence order (227-228). They are now killed.

## Still standing after the change (17 Survived, 4 NoCoverage)

None of these is a silent failure.

| Line | Status | Mutant | Disposition and reason |
| ---: | --- | --- | --- |
| 41 | Survived | install-root request kind `"tenant"` to `""` | **Equivalent.** Both production closure readers (`NodeEfAuthorizationClosureReader`, `DefinitionJoinedAuthorizationReader`) key on `Target.Scope` and never read `Target.RecordKind`. `InstallRootPermissionsAsync` yields candidates only; `SelectedSessionPermissionResolver` and `ActiveTeamAuthorizationContext` decide each one through `DecideAsync`. `AdminTeamAccessAuthority` lists them for display. The baseline counted it Killed by `FormsStartupCapturedIdentityFenceTests.DesktopPlaneSubmit_StillTracksTheOperatorsGrants`, which does not repeat (see the note below). |
| 42, 72, 91, 223 | Survived | `ConfigureAwait(false)` to `true` | **Equivalent.** There is no synchronization context in the host or the tests. The baseline "kill" of 42 (`MemberOutcome_DoesNotTrackTheOperatorsStartupRole`) is the same noise. |
| 224 | Survived | `ct.ThrowIfCancellationRequested()` in the attenuation loop removed | **Equivalent in outcome.** The `ThrowIfCancellationRequested` before the decision is built still throws, so no decision is returned after cancellation; only the timing moves. |
| 144 | Survived | `requireRosterMember = false` to `true` | **Equivalent: dead store.** The local is not read after line 144; the effective change is line 147, which is Killed. |
| 232 | Survived | `??` remove-left on `authorization.grant.attenuation_failed` | **Equivalent.** Line 124 clears `GrantRefusal` to null, and this constant is the only value ever assigned, so both sides are equal. |
| 175 (x2) | NoCoverage | divergence invariant `throw` removed / message emptied | **Unreachable.** Both readings come from the same atoms, so they cannot diverge. |
| 205 (x2) | NoCoverage | `RequiredPermissions ... Any` to `All`; `==` to `!=` | **Unreachable.** The gate sets `RequiredPermissions = PermissionSet.Empty` (line 121), so the outer `All` over an empty set never calls the lambda. |
| 68, 88, 278, 295, 301 (message), 306, 331 (message), 336 (message) | Survived | exception message text emptied | **Out of contract: message text.** Each throw is killed above; the exception type and the fact of refusal are the contract, not the wording. |
| 292 | Survived | pack-target kind `"asset-type"` emptied | **Out of this filter, fails closed.** Emptying it makes the gate refuse type authoring. With the mutant applied by hand, `AssetRegistryRouteTests` fails 20+ tests, including `types: denied packages:author cannot create a type or persist it`. That class is outside the Authorization/Identity filter. |

Note on 41 and 42: in the baseline the two `FormsStartupCapturedIdentityFenceTests` tests "killed" a `ConfigureAwait` mutant, which cannot change behaviour. Both also survived in #269's run and in the after run, so the baseline kills are treated as run noise, not evidence.

## Out of this project

- **`AuthorizationTraceReader`** is in `packages/kernel-audit`. The host test project reaches it only transitively, so Stryker cannot mutate it from this config (AGENTS.md "One config mutates one direct reference"). Measuring it needs a direct `ProjectReference`, as #256 added for `packages/foundation-authorization`.
- **`AuthorizationDecisionEvidence.cs`** is in this project. This change measures only the gate's evidence-producing branches, not that file.

## Baseline ratchet

No `eng/baselines/mutation-baseline.json` slice covers this file. The host slices in `eng/baselines/mutation-slices.json` match paths relative to `apps/local-node-host`, and `packages/foundation-authorization` is not a configured test project, so nothing moves.

## Host tests

`dotnet test apps/local-node-host/tests/tests.csproj` with filter `FullyQualifiedName~...Tests.Authorization.|FullyQualifiedName~...Tests.ArchTests.`: 929 tests, 914 passed, 15 failed. None of the failures is this change's:
- 14 `AuthorizationAdminRouteTests` fail in `InitializeAsync` with `SocketException: Address family not supported by protocol`, because Kestrel cannot bind in a container without IPv6.
- `ControlHintDispatchFenceTests` (T-664) walks the filesystem and flags the `.platform/` checkout's own platform test files. With `.platform` moved out of the tree it passes, 7/7.

`AuthorizationGateTests` passes 57/57.

## Raw reports

The raw reports are not tracked, for the size and scanner reasons in AGENTS.md "Mutation testing". They are archived on the orphan branch `archive/ck5-gate-survivors-2026-09-29`, with a `SHA256SUMS` file.

| Report | SHA-256 |
| --- | --- |
| After (this change), `reports/after.json` | `3316b435b41a8fa4c5b1e09a6f40e47c960e569810ea42de01cba8ff416ab3f2` |
| Before (main `27dbc9c`), `reports/before.json` | `3d4b69214e208408f250234e549cbc07f0dff254691d62cb91b664bb80c73dad` |
