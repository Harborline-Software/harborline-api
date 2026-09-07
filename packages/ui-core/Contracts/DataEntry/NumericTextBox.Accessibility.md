# NumericTextBox — Accessibility Contract

- **Component:** NumericTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericTextBox.Semantic.md) · [Interaction](./NumericTextBox.Interaction.md) · [Styling](./NumericTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericTextBox.tsx`
- **Catalog row:** #90 NumericTextBox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `id` | `<input>` | For `<label htmlFor>` association |
| `name` | `<input>` | Form participation |
| `disabled` | `<input>` | Disabled state |
| `readOnly` | `<input>` | Readonly state |
| `aria-label="Increment"` | Spinner up `<button>` | Accessible label |
| `aria-label="Decrement"` | Spinner down `<button>` | Accessible label |
| `tabIndex={-1}` | Spinner `<button>` | Not tab-reachable |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-NTB1 | Medium | `aria-invalid` not set | Accepted-risk M1 |
| G-NTB2 | Medium | No `aria-describedby` | Accepted-risk M1 |
| G-NTB5 | Medium | Input type switches between `text` and `number` on focus — AT may announce the input type change | Accepted-risk M1 |
| G-NTB3 | Low | Spinners are `tabIndex={-1}` — not accessible via keyboard Tab | Accepted-risk M1 |
