# C-008: independent shutdown budget and retirement of the retry allowance

The real host-boot smoke fact gave startup and shutdown the same 30-second cancellation token. This change starts a new 30-second shutdown budget only when shutdown begins. The production registration graph and its worker/endpoint identity, health, route and sealing assertions remain unchanged. The original negative DI-cycle fixture remains unchanged.

`Shutdown_Budget_Is_Independent_Of_Expired_Startup` drives an actual `IHost` with a hosted-service observer. Its literal false case cancels startup's token and verifies that the old shared-token call throws the exact observer exception; its literal true case runs the same shutdown helper used by the real smoke fact and verifies one stop with an uncanceled token. Explicit cancellation, the unique exception message and the observer's token state are the oracles; there is no timing sleep or expected value derived from production output. The cases executed in the initial Linux host gate described below; independent review and final combined-head validation remain pending. No local heavy build was run.

## Executed diagnostic evidence

Pre-fix source: diagnostic PR [#376](https://github.com/Harborline-Software/harborline-api/pull/376), head `6870c8eba8c5c07cbf2e8afcc23814f925201c62`, tree `fe63ce22ee47c2f191a18d8466d0f90c39b71d99`. [Exact-head independent review](https://github.com/Harborline-Software/harborline-api/pull/376#issuecomment-6020277215) found no actionable comments before execution.

[Windows run 37494206792](https://github.com/Harborline-Software/harborline-api/actions/runs/37494206792) completed successfully in 5m28s on SDK `11.0.100-rc.1.26425.128`. All 24 acceptance checks passed: 20 measured positive samples (five each shared/separate token under quiet/two-worker CPU load); two explicit cancellation controls; and the original negative DI-cycle fact under quiet and load. Raw TRX counters and identities were inspected: 23 passed, one intentionally failed, zero skipped, one exact selected test per invocation.

The expired-shared control started successfully at 113.1278ms, entered shutdown with a canceled token at 114.9484ms and threw `OperationCanceledException: C008 probe observed expired shutdown token` at 120.7169ms. The expired-separate control started at 109.9501ms and completed shutdown at 119.7499ms with an uncanceled token. This proves the shared-token shutdown hazard when cancellation is deliberately injected. It does not establish the cause of C-008's historical **startup** cancellation.

Raw evidence: [artifact 11427690322](https://github.com/Harborline-Software/harborline-api/actions/runs/37494206792/artifacts/11427690322). ZIP upload SHA-256 `7343d47d92e1507d88b76f0638d1bf8702303f62a2487723a462fd63bfb9095f`; downloaded `result.json` SHA-256 `a531c020f6faaeca4fd1bb74682a194a10c0e2e56590f222dda36e334509ea2d`. Artifact retention is 14 days. This is diagnostic evidence, not a full-host gate or mutation report.

Actual phase durations (end minus begin, milliseconds) are below. Load means two arithmetic worker threads running concurrently with the selected test, not the historical concurrent frontend Vitest suite.

| Mode | Samples | Startup range (ms) | Shutdown range (ms) |
| --- | ---: | ---: | ---: |
| Shared, quiet | 5 | 94.9643–121.9154 | 6.3006–11.1733 |
| Shared, load | 5 | 104.8744–112.5488 | 8.3994–14.2755 |
| Separate, quiet | 5 | 96.1509–100.3994 | 6.3856–11.1843 |
| Separate, load | 5 | 108.6483–121.3154 | 7.8271–9.8936 |

## Actual policy and decision

`eng/flake-registry.mjs` explicitly allows the registry to shrink: its count is a ceiling, not an equality. It requires exact, owned, dated, unexpired rows and at most one identical retry; an expired row is red. `eng/tests/flake-registry.test.mjs` additionally limits each registration to 60 days from first sight. No general ten-clean-landings requirement is present in these rules; T-361's ten-landings requirement applied to its separate MD-2 G-4 repair.

An exact comparison of the original C-008 row against the diagnostic TRX display identity also found an existing mismatch in the dash spelling. This change removes that obsolete registration; it does not normalize it into a new rescue allowance. The test stays enforced by its actual identity.

Retire C-008's Windows `knownFlaky` row and reduce the ceiling from four to three. The smoke fact stays enabled, retains its assertions and remains in the known-test roster. It is not added to permitted failures or skipped tests. `eng/run-exact-clone.mjs` lines 416–494 admit retries only for exact registered unexpected failures; after retirement any host-boot failure is an unregistered NEW failure and fails the gate on its first result. Retirement removes a retry privilege; it does not assert the historical failure can never recur or that this repair explains it.

The evidence supports strict enforcement with this residual risk: twenty repetitions on one ephemeral Windows host and one synthetic CPU profile did not recreate the historical concurrent frontend Vitest workload. Required host validation on the final repair head and its normal protected merge candidate must still pass. Recurrence is actionable evidence, not an automatic re-registration. No dates, other flake owners, permitted failures or skip counts are changed. A future renewal would require a new owned, dated reason and review; there is no blind expiry extension in this change.

Lightweight validation of this prepared repair: `node --test eng/tests/flake-registry.test.mjs eng/tests/host-baseline.test.mjs eng/tests/repin-baseline.test.mjs` passed 57 tests with zero failures or skips on 2026-10-06. These verify the registry/baseline contracts; they do not execute the new C# cases.

## Required dependency prerequisite

The current main branch's `source-map-js` 1.2.1 audit failure prevents the repair's shared required lane from passing: run `37496159592`, job `112381127121`, failed only at `dependency-ledger` with both pnpm audits exiting 1. PR #375 independently reviewed head `623888d12909533f7e712feaccbd1577967778c0` patches the advisory-scoped override to 1.2.2 in the two projects and retains required focused coverage-off/on proof for the four exact pnpm inputs. Its own Windows gate still carries the expired C-008 row. Both corrections therefore need to be present in one protected landing.

This repair includes those six prerequisite files unchanged from the reviewed #375 head. Their Git blobs were compared exactly before publication. No workflow, threshold, baseline allowance, other flake date or audit policy is changed by the prerequisite. Combined lightweight validation, adding `eng/tests/focused-mode-policy.test.mjs` to the command above, passed 143 tests with zero failures or skips. Final-head independent review and required hosted validation remain mandatory; the earlier shared-lane failure is not treated as a passing receipt.

The initial repair's Linux host job `112381127438` in run `37496159592` completed successfully at source head `3bc5ce4ecaf27c6d7acb8cdfaadfb005334c09ba`, tested tree `9d8f71be4cd87c424006a1117852f50accda198b`. Its executed-identity artifact contains both literal theory cases (`separateBudget: False` and `True`), and the gate reported zero NEW failures or rescues under the unchanged Ubuntu baseline. The four permitted failures were unrelated existing identities; neither new case is permitted. The full-host identity evidence therefore establishes both new cases executed and passed, rather than being skipped or rescued. It is not an overall passing verify run: the shared audit step failed as described above. `known-tests-candidate.json` SHA-256: `2bb1052d646927ae249992d4be2d05857e04b6d726e6da5ea387c53ad9d330a2`.

The host fixture Git blob `74e3b7b7dee865cc49a0b3933f3ac393159e14ed` is unchanged by the dependency consolidation. Final combined-head host validation is still required because dependency inputs changed. The #375 Windows job `112363113383` completed with zero NEW failures, zero rescues and only the expired C-008 registration failing; its process trace showed continued test completions and CPU activity during the 45m19s full-host test phase, rather than a frozen test process.
