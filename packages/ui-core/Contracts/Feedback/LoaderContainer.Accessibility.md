# LoaderContainer — Accessibility Contract

- **Component:** LoaderContainer
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoaderContainer.Semantic.md) · [Interaction](./LoaderContainer.Interaction.md) · [Styling](./LoaderContainer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/LoaderContainer.tsx`
- **Catalog row:** #80 LoaderContainer (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-busy` | Container `<div>` | `true` while loading; `false` otherwise |
| *(delegated)* | Inner `<Loader>` | See Loader accessibility contract |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LC2 | High | Container did not expose its loading state to AT | Resolved 2026-07-15: container reflects loading through `aria-busy` |
| G-LC3 | High | No `aria-live` region announces loading state change — screen reader users receive no feedback | Accepted-risk M1 |
| G-LC4 | Medium | Children remain focusable via keyboard while obscured by overlay — keyboard users can interact with non-visible elements | See G-LC1 in Interaction contract; Accepted-risk M1 |
