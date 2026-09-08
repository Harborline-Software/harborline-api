# Toolbar — Interaction Contract

- **Component:** Toolbar
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Toolbar.Semantic.md) · [Accessibility](./Toolbar.Accessibility.md) · [Styling](./Toolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Toolbar.tsx`
- **Catalog row:** #139 Toolbar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Interaction model

Toolbar is a **layout primitive**. It has no built-in interaction — it arranges `leading` / `children` / `trailing` slots.

All interactive behavior comes from the components placed inside the slots (buttons, dropdowns, search inputs, etc.).

---

## 2. Known gaps

None. Toolbar does not own any interactions.
