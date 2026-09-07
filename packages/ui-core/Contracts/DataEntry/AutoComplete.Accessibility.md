# AutoComplete — Accessibility Contract

- **Component:** AutoComplete
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AutoComplete.Semantic.md) · [Interaction](./AutoComplete.Interaction.md) · [Styling](./AutoComplete.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AutoComplete.tsx`
- **Catalog row:** #7 AutoComplete (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="combobox"` | `<input>` | WAI-ARIA combobox widget |
| `aria-expanded={open}` | `<input>` | Dropdown open/closed |
| `aria-autocomplete="list"` | `<input>` | AT knows completions come from a list |
| `role="listbox"` | Suggestions `<ul>` | Option list |
| `role="option"` | Each suggestion `<li>` | Selectable option |
| `aria-selected={i === activeIdx}` | Each `<li>` | Active/highlighted state |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AC2 | High | No `aria-controls` on input — AT cannot navigate from combobox to listbox | Accepted-risk M1 |
| G-AC3 | High | No `aria-activedescendant` — AT won't announce the highlighted suggestion as the user arrows down | Accepted-risk M1 |
| G-AC5 | Medium | No `id` on listbox `<ul>` — needed for `aria-controls` (linked to G-AC2) | Accepted-risk M1 |
| G-AC6 | Low | Loading indicator (`⟳`) has no `aria-live` announcement — AT won't know results are loading | Accepted-risk M1 |
