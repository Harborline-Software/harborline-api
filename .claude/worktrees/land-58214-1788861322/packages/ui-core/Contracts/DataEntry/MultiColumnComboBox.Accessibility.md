# MultiColumnComboBox — Accessibility Contract

- **Component:** MultiColumnComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiColumnComboBox.Semantic.md) · [Interaction](./MultiColumnComboBox.Interaction.md) · [Styling](./MultiColumnComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiColumnComboBox.tsx`
- **Catalog row:** #85 MultiColumnComboBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="text"` | Trigger `<input>` | Text input |
| `role="combobox"` | Trigger `<input>` | Identifies as combobox |
| `aria-expanded={open}` | Trigger `<input>` | `true` when dropdown open |
| `disabled` | Trigger `<input>` | When disabled |

---

## 2. Keyboard navigation

Arrow keys move `activeIdx` through filtered rows. The highlighted row gets `bg-accent text-accent-foreground`. AT is not informed of the highlighted row (no `aria-activedescendant`) — Gap G-MCCB3.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MCCB3 | High | No `aria-activedescendant` on the input — AT cannot track which row is keyboard-highlighted | Accepted-risk M1 |
| G-MCCB4 | High | Dropdown rows use `<tr>` with no `role="option"` — AT doesn't understand these as listbox options | Accepted-risk M1; table structure provides some column context but ARIA listbox pattern is missing |
| G-MCCB5 | Medium | No `aria-controls` linking input to the dropdown panel | Accepted-risk M1 |
