# Callout — Accessibility Contract

- **Component:** Callout
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Callout.Semantic.md) · [Interaction](./Callout.Interaction.md) · [Styling](./Callout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Callout.tsx`
- **Catalog row:** (not-in-catalog) Callout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="alert"` | Container `<div>` | Only on `variant='error'` — assertive live region |
| *(no role)* | Container `<div>` | For `info`, `tip`, `warning` — generic block, no live region |
| `aria-hidden="true"` | Default icon SVGs | Decorative icons |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CO1 | Medium | `info`/`tip`/`warning` variants have no live region role — not announced to AT on dynamic insertion | Accepted-risk M1; Callout is primarily static documentation content |
| G-CO2 | Low | Custom `icon` prop: caller-provided icons don't have `aria-hidden` enforced | Accepted-risk M1 |
