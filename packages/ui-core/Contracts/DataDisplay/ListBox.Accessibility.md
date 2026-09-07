# ListBox — Accessibility Contract

- **Component:** ListBox
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListBox.Semantic.md) · [Interaction](./ListBox.Interaction.md) · [Styling](./ListBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListBox.tsx`
- **Catalog row:** #77 ListBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="listbox"` | `<ul>` container | ARIA listbox |
| `aria-multiselectable={selection === 'multiple'}` | `<ul>` | Multi-select mode flag |
| `role="option"` | Each `<li>` item | ARIA option |
| `aria-selected={selected.includes(item.value)}` | Each `<li>` | Selection state |
| `aria-disabled={item.disabled}` | Disabled `<li>` | Disabled state |
| `aria-label="Move up"` | Toolbar Move Up button | Accessible label |
| `aria-label="Move down"` | Toolbar Move Down button | Accessible label |
| `aria-label="Remove"` | Toolbar Remove button | Accessible label |

---

## 2. Focus management

List items are not individually focusable in M1 (no `tabIndex`). The `<ul>` itself has no `tabIndex` either. Keyboard users cannot access list items via keyboard.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LB4 | Critical | No `tabIndex` on list or items — keyboard users cannot focus or navigate the listbox | Accepted-risk M1; design assumes pointer use |
| G-LB5 | High | `aria-activedescendant` not set on listbox — AT cannot track focused item | Accepted-risk M1 |
| G-LB6 | Medium | No `aria-label` on the `<ul>` itself — list purpose not announced to AT | Accepted-risk M1 |
