# StepList — Semantic Contract (CONSOLIDATED)

- **Component:** StepList
- **Status:** Accepted — superseded by consolidation
- **Superseded by:** [Layout/Stepper.Semantic.md](../Layout/Stepper.Semantic.md)
- **CIC ruling:** 2026-06-12, KS-6 review Q3 — "Consolidate + delete"

---

## Notice

**StepList has been consolidated into Stepper.**

StepList and Steps were near-identical step-progress display components. Per CIC ruling
KS-6 Q3 (2026-06-12), both have been consolidated into `Stepper` (catalog row #128),
which is now the single canonical step-indicator component.

**If you were using StepList**, migrate to `Stepper`:

| StepList prop | Stepper equivalent | Notes |
|---|---|---|
| `steps: Step[]` (with `id: string`) | `steps: StepperStep[]` (with `value: string`) | Rename `id` to `value` |
| `currentStep?: number` | `currentStep?: number` | Same semantics |
| `orientation` | `orientation` | Identical |
| `status: 'pending'\|'active'\|'complete'\|'error'` | `status: 'pending'\|'active'\|'complete'\|'error'` | Identical — Stepper adopted StepList's enum |

See [Layout/Stepper.Semantic.md](../Layout/Stepper.Semantic.md) for the full API.
