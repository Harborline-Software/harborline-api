# ErrorCard — Interaction Contract

- **Component:** ErrorCard
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ErrorCard.Semantic.md) · [Accessibility](./ErrorCard.Accessibility.md) · [Styling](./ErrorCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ErrorCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Retry button

| Condition | Trigger | Effect |
| --- | --- | --- |
| `onRetry` provided | Retry button click | `onRetry()` |
| `onRetry` absent | — | No button rendered |

---

## 2. Display-only (no retry)

When `onRetry` is absent, ErrorCard is purely display-only — no user
interaction.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-EC1 | Low | No loading/disabled state on Retry button — double-click can fire multiple retries | Accepted-risk M1 |
