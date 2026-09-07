# Input — Interaction Contract

- **Component:** Input
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Input.Semantic.md) · [Accessibility](./Input.Accessibility.md) · [Styling](./Input.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Input.tsx`
- **Catalog row:** #72 Input (`app-priority: critical`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

Input is a thin styled wrapper — all interaction is delegated to the native `<input>` element. Callers manage value via `value`/`onChange` (controlled) or `defaultValue` (uncontrolled).

The component adds no interaction handlers beyond `className`.

---

## 2. Known gaps

None.
