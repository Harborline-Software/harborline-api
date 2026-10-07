# Zero registered flake allowances

The owner requested `REGISTERED_FLAKE_COUNT = 0` on 7 October 2026. This is a stricter
enforcement decision, not a claim that intermittent failures cannot recur. The
constant is a per-host registry ceiling, not an observed flake count or a clean
landing count. `RETRY_LIMIT` is now also zero; no environment switch grants a retry.

The one remaining identity was
`Harborline.Api.LocalNodeHost.Tests.Installation.InstallIdentityTests.ConcurrentFirstLaunchers_ObserveOneInstallIdentity`.
At protected source `29ab265155bee953a17bbd25a94cd22b7bea6118`, its Windows row was
owned by 348, first seen 2026-09-08, expiring 2026-10-08. Its macOS row was owned by
361, first seen 2026-09-09, also expiring 2026-10-08. The historical macOS narrative
records a red run at load 18 and a green rerun at load 3 on 2026-09-09. That old
observation is not a current native flake rate or verified clean-landing count.

Both rows are removed; all three host registries are empty. The existing `[Fact]`
and exact identity remain enabled and in every committed host roster. No test,
skip, permitted-failure row, baseline count, expiry or threshold is relaxed. The
gate's production retry planner selects no retries, and an unexpected initial
failure remains NEW. The baseline repinner also refuses historical reinserted rows
and cannot count the retired failure green.

Recent [private run 37652414688](https://github.com/Harborline-Software/harborline-control/actions/runs/37652414688)
recorded this exact identity Passed in both Linux coverage modes. Each raw
`host-flake-retry` step had empty retry identities/attempts and `rescued: 0`; Ubuntu
already had no registered row. [Hosted attempt 37652134396/1](https://github.com/Harborline-Software/harborline-api/actions/runs/37652134396/attempts/1)
also logged zero Linux rescues. That overall attempt failed without creating the
aggregate job; its cause remains unconfirmed and failed evidence is preserved.
These Linux observations supply no verified Windows/macOS clean-landing count.
The separate booking/timeout incidents are not registration-matching proof here.

Literal zero-policy tests exercise the production planner and initial-failure
baseline verdict; real-repinner tests preserve baseline bytes on refusal. Final
source review and protected PR/merge-group checks remain required. Removing retry
privileges does not authorize product release or resolve historical incident holds.
