# MaskedText — Interaction Contract

- **Component:** MaskedText
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedText.Semantic.md) · [Accessibility](./MaskedText.Accessibility.md) · [Styling](./MaskedText.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/MaskedText.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Reveal / hide toggle

| State | Trigger | Effect |
| --- | --- | --- |
| `revealed=false` (default) | Toggle button click | `revealed → true`; full value shown; button icon switches to "hide" (eye-slash) |
| `revealed=true` | Toggle button click | `revealed → false`; masked value shown; button icon switches to "show" (eye) |

The toggle button is `<button type="button">`. Reveal state is internal to
the component and resets if the component unmounts and remounts.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-MT1 | Low | No keyboard shortcut beyond Tab+Enter/Space to toggle | Accepted-risk M1 |
| G-MT2 | Low | Revealed state persists within a mounted session — no auto-hide timeout | Accepted-risk M1 |
