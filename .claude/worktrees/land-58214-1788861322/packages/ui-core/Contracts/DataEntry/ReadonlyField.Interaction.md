# ReadonlyField — Interaction Contract

- **Component:** ReadonlyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ReadonlyField.Semantic.md) · [Interaction](./ReadonlyField.Interaction.md) · [Accessibility](./ReadonlyField.Accessibility.md) · [Styling](./ReadonlyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ReadonlyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

ReadonlyField has no interactive behaviour. It is a pure display component.
This contract is intentionally minimal.

---

## 2. Behaviour

ReadonlyField renders a label/value pair. There are no click handlers, no
focus management, no event callbacks, and no internal state.

---

## 3. No known interaction gaps

ReadonlyField has no interaction gaps — it is intentionally non-interactive.
