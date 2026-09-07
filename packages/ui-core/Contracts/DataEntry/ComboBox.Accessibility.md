# ComboBox — Accessibility Contract

- **Component:** ComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ComboBox.Semantic.md) · [Interaction](./ComboBox.Interaction.md) · [Styling](./ComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ComboBox.tsx`
- **Catalog row:** #32 ComboBox (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="combobox"` | `<input>` | Combobox role |
| `aria-expanded={open}` | `<input>` | Dropdown open state |
| `role="listbox"` | `<ul>` | Option container |
| `role="option"` | `<li>` | Each option |
| `aria-selected={item.value == current}` | `<li>` | Selected option |
| `aria-disabled={item.disabled}` | `<li>` | Disabled option |
| `tabIndex={-1}` | Toggle `<button>` | Not keyboard-reachable directly |

---

## 2. Known gaps

Canonical gap table lives in [ComboBox.Semantic.md §7](./ComboBox.Semantic.md). Accessibility-relevant excerpts with authoritative dispositions:

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CBX1 | High | `aria-controls` missing on input — AT cannot find the listbox | **Blocking-before-v1-ship** — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-CBX2 | High | `aria-activedescendant` missing on input — active item not announced on ArrowDown/Up | **Blocking-before-v1-ship** — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-CBX3 | Medium | `aria-autocomplete="list"` missing | Accepted-risk M1 |
| G-CBX5 | Medium | Toggle button has no `aria-label` — AT reads it as `'▾'` or `'⟳'` | Accepted-risk M1 |
| G-CBX6 | Low | No `aria-label` or `aria-labelledby` on input — callers must supply external `<label>` | Accepted-risk M1 |
