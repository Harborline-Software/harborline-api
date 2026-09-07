# AspectRatio — Accessibility Contract

- **Component:** AspectRatio
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AspectRatio.Semantic.md) · [Interaction](./AspectRatio.Interaction.md) · [Styling](./AspectRatio.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 AspectRatio (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix AspectRatio baseline)

---

## 1. ARIA roles

No ARIA role. AspectRatio is a layout container — no semantic meaning to communicate to AT.

---

## 2. Content accessibility

All accessibility responsibilities belong to the caller's children. Images inside AspectRatio require `alt` attributes; videos require captions; maps require descriptive text alternatives.

---

## 3. Known gaps

None identified.
