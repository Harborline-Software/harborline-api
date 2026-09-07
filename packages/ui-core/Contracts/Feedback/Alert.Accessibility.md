# Alert — Accessibility Contract

- **Component:** Alert
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Alert.Semantic.md) · [Interaction](./Alert.Interaction.md) · [Styling](./Alert.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Alert.tsx`
- **Catalog rows:** #153 Alert / #78 CalloutBox (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. ARIA roles

| Attribute | Element | Value |
|---|---|---|
| `role` | Alert container `<div>` | `'alert'` for `warning`/`error`; `'status'` for `info`/`success` |
| `aria-hidden="true"` | Variant icon SVG | Icon is decorative |
| `type="button"` | Close button | Explicit button type |
| `aria-label="Dismiss"` | Close button | Accessible label |

---

## 2. Live region behavior

`role="alert"` is an assertive live region — AT announces immediately on mount. `role="status"` is a polite live region — AT announces at the next opportunity.

| Variant | Role | Urgency |
|---|---|---|
| `error` | `alert` | Assertive (interrupts) |
| `warning` | `alert` | Assertive (interrupts) |
| `info` | `status` | Polite |
| `success` | `status` | Polite |

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AL1 | Low | Alert rendered at page load may not be announced by AT (live regions must be present before content changes) | Accepted-risk M1 |
| G-AL2 | Low | No `aria-live` / `aria-atomic` explicit attributes — relies on implicit behavior of `role="alert"` and `role="status"` | Accepted-risk M1 |
