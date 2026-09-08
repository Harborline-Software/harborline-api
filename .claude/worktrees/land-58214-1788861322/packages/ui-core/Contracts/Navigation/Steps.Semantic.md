# Steps — Semantic Contract (CONSOLIDATED)

- **Component:** Steps
- **Status:** Accepted — superseded by consolidation
- **Superseded by:** [Layout/Stepper.Semantic.md](../Layout/Stepper.Semantic.md)
- **CIC ruling:** 2026-06-12, KS-6 review Q3 — "Consolidate + delete"

---

## Notice

**Steps has been consolidated into Stepper.**

Steps and StepList were near-identical step-progress display components. Per CIC ruling
KS-6 Q3 (2026-06-12), both have been consolidated into `Stepper` (catalog row #128).

**If you were using Steps**, migrate to `Stepper`:

| Steps prop | Stepper equivalent | Notes |
|---|---|---|
| `steps: Step[]` (with `id: string`) | `steps: StepperStep[]` (with `value: string`) | Rename `id` to `value` |
| `status: 'complete'\|'current'\|'upcoming'` | `status: 'complete'\|'active'\|'pending'` | Rename `current` → `active`, `upcoming` → `pending` |
| `orientation` | `orientation` | Identical |
| (no `currentStep`) | `currentStep?: number` | New convenience prop for index-based derivation |

The Steps component had no index-based convenience prop (all statuses were explicit per step).
With Stepper you can use either explicit `status` per step OR the `currentStep`/`activeStep`
derivation props.

See [Layout/Stepper.Semantic.md](../Layout/Stepper.Semantic.md) for the full API.
