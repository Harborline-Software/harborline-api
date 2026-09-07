# NotificationDot — Accessibility Contract

- **Component:** NotificationDot
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationDot.Semantic.md) · [Interaction](./NotificationDot.Interaction.md) · [Styling](./NotificationDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NotificationDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `aria-label` | Inner dot `<span>` | `aria-label` prop value (when provided) |
| `role="status"` | Inner dot `<span>` | Added only when `aria-label` is provided |
| `aria-hidden="true"` | Pulse ring `<span>` | Decorative animation ring |

When no `aria-label` is provided, the dot has no role and is effectively
decorative — the accessible semantics are carried by the `children` element.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-ND1 | Low | Without `aria-label`, the dot is invisible to AT — host must supply `aria-label` for any dot that carries meaningful information | Accepted-risk (by design — optional) |
| G-ND2 | Low | `role="status"` is a polite live region; if the dot appears dynamically (visible → true), AT may not announce it immediately | Accepted-risk M1 |
