# GridLayout — Accessibility Contract

- **Component:** GridLayout
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GridLayout.Semantic.md) · [Interaction](./GridLayout.Interaction.md) · [Styling](./GridLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/GridLayout.tsx`
- **Catalog row:** #67 GridLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

No ARIA attributes added. GridLayout and GridLayout.Item render plain `<div>` elements. Layout is purely visual; semantic meaning comes from the child content.

---

## 2. Known gaps

None. Layout primitives do not require ARIA augmentation.
