# AspectRatio — Interaction Contract

- **Component:** AspectRatio
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AspectRatio.Semantic.md) · [Accessibility](./AspectRatio.Accessibility.md) · [Styling](./AspectRatio.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 AspectRatio (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix AspectRatio baseline)

---

## 1. Interaction model

AspectRatio is a pure layout component. No interactive states, event handlers, or keyboard behavior. All interactivity is delegated to children.

---

## 2. Known gaps

None identified.
