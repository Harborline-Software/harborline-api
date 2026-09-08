# Stepper — Interaction Contract

- **Component:** Stepper
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Stepper.Semantic.md) · [Accessibility](./Stepper.Accessibility.md) · [Styling](./Stepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Stepper.tsx`
- **Catalog rows:** #160 Stepper / #48 Stepper (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. Interaction model

Stepper is a **read-only progress indicator**. It has no interactive elements — no clicks, no keyboard handlers.

Navigation between steps is managed entirely by the parent (by changing the `activeStep` prop). The Stepper renders the current state only.

---

## 2. Status derivation

When `step.status` is not provided, status is derived from `activeStep`:

| Condition | Derived status |
|---|---|
| `stepIndex < activeIndex` | `'completed'` |
| `stepIndex === activeIndex` | `'active'` |
| `stepIndex > activeIndex` | `'pending'` |
| `activeStep` not found | All steps `'pending'` |

Explicit `step.status` always overrides derivation.

---

## 3. Known gaps

None. Stepper is intentionally non-interactive.
