# CheckBox — Accessibility Contract

- **Component:** CheckBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CheckBox.Semantic.md) · [Interaction](./CheckBox.Interaction.md) · [Styling](./CheckBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CheckBox.tsx`
- **Catalog row:** #24 Checkbox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

Native `<input type="checkbox">` provides the `role="checkbox"` implicitly.

| Attribute | Element | Value |
|---|---|---|
| `type="checkbox"` | `<input>` | Native role |
| `checked` / `defaultChecked` | `<input>` | Checked state |
| `disabled` | `<input>` | Disabled state |
| `required` | `<input>` | Required state |
| `id` | `<input>` | For external `<label htmlFor>` association |

Label association: when `label` prop is provided, the `<label>` wraps the `<input>` (implicit association). When no label is provided, callers must supply an external `<label htmlFor={id}>` or `aria-label`.

---

## 2. Indeterminate state

The indeterminate state is communicated via `inputRef.current.indeterminate = true` (DOM property). Browsers expose this to AT as `aria-checked="mixed"` automatically.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CB5 | Medium | `aria-invalid` is not set on the checkbox input — invalid state requires a parent wrapper to set it | Accepted-risk M1 |
| G-CB6 | Low | No `aria-describedby` prop — callers cannot link hint/error text to the checkbox without a wrapping FormField | Accepted-risk M1 |
| G-CB7 | Low | No label when `label` prop absent — standalone `CheckBox` without an `id` is unlabelled | Accepted-risk M1 |
