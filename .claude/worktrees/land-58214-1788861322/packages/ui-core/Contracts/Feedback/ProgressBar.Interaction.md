# ProgressBar — Interaction Contract

- **Component:** ProgressBar
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ProgressBar.Semantic.md) · [Accessibility](./ProgressBar.Accessibility.md) · [Styling](./ProgressBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ProgressBar.tsx`
- **Catalog row:** #100 ProgressBar (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

ProgressBar is a **read-only display component**. No user interactions.

Progress value is driven entirely by the `value` prop. The component re-renders as the caller updates the prop — no internal state.

---

## 2. Animation

`animation=true` (default): fill uses `transition-all duration-500 ease-in-out` on value changes. `animation=false`: no transition class applied.

Indeterminate + animation: fill uses `animate-pulse` instead of transition.

---

## 3. Known gaps

None.
