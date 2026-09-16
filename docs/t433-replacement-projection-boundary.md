# T433 replacement: observed projection boundary defect

The selected-session replacement action must reuse immutable Draft installation followed by governed activation. A refused draft may remain as evidence; the previously active pack and its effective definitions must remain unchanged when activation/projection refuses.

## Reproduction

`AccessAdministrationPreloadTests.Replacement_late_view_refusal_keeps_old_active_catalogue_and_publishes_no_early_view` preloads the real Platform/Access packs, installs a signed replacement with an early valid view followed by a late unknown-view-kind, and invokes the production installer/projector.

Observed on API main ancestry `539ec9e8` plus T433 producer commit `56429e43`: the late view is refused, but `m6.early-view` is already in the live view registry. The focused regression is deliberately red. Evidence: `artifacts/t433-action-contract/replacement-atomicity-red.log`.

`PackInstaller.ActivateCore` flips the active pointer and records admission before `ProjectAndRetire`. `PackSeedProjector` registers items during its admission loop and only delays predecessor retirement until refusals are empty. A late refusal therefore neither prevents earlier publication nor restores the active pointer. Retrying the previous pack is not a byte-identical rollback of newly added revisions or lifecycle timestamps.

T454's `pendingDefaults` staging protects Cascade Defaults only. It does not make other registries or the active pack atomic.

## Ownership boundary

The central projection owner owns the single projector-wide preparation/commit/rollback seam, pack activation critical section, registry/lifecycle transaction support, and this regression after handoff. Reuse existing admission/projector semantics; do not create a second projector or per-kind late-refusal patches.

T433 action-consumer work retains the closed selected-session replacement endpoint, response receipt, route registration, generic descriptor/binding/compiler metadata, signed fixtures, and App generic action host. Its endpoint must expose draft installation and activation separately, active version/definition sets before and after, and actual server audit evidence. It must not describe a pending or partial projection as a successful replacement.
