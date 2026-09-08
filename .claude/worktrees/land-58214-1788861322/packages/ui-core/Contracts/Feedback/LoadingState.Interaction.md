# LoadingState — Interaction Contract

- **Component:** LoadingState
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoadingState.Semantic.md) · [Accessibility](./LoadingState.Accessibility.md) · [Styling](./LoadingState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/LoadingState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

LoadingState is purely informational. No clickable elements, no events,
no state transitions within the component.

---

## 2. Known gaps

None. Component is intentionally display-only.
