# StackLayout — Accessibility Contract

- **Component:** StackLayout
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StackLayout.Semantic.md) · [Interaction](./StackLayout.Interaction.md) · [Styling](./StackLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/StackLayout.tsx`
- **Catalog row:** #127 StackLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

No ARIA attributes added. StackLayout renders a plain `<div>`. Semantic meaning comes from child content.

---

## 2. Known gaps

None. Layout primitives do not require ARIA augmentation.
