# GlobalSearch — Accessibility Contract

- **Component:** GlobalSearch
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GlobalSearch.Semantic.md) · [Interaction](./GlobalSearch.Interaction.md) · [Accessibility](./GlobalSearch.Accessibility.md) · [Styling](./GlobalSearch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/GlobalSearch.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| `<input>` | `role` | `combobox` |
| `<input>` | `aria-expanded` | `true` when dropdown is open and results exist, `false` otherwise |
| `<input>` | `aria-autocomplete` | `"list"` |
| `<input>` | `aria-haspopup` | `"listbox"` |
| Dropdown `<div>` | `role` | `listbox` |
| Dropdown `<div>` | `aria-label` | `"Search results"` |
| Result `<button>` | `role` | `option` |
| Result `<button>` | `aria-selected` | `true` when keyboard-active, `false` otherwise |
| Icon `<span>` | `aria-hidden` | `"true"` |

---

## 2. Keyboard accessibility

All keyboard behaviours from the Interaction contract are keyboard-accessible per the WAI-ARIA combobox pattern. The `ArrowDown` / `ArrowUp` / `Enter` / `Escape` keys are handled on the input element.

---

## 3. Screen reader behaviour

- The search icon is `aria-hidden="true"` — decorative.
- Result icons are `aria-hidden="true"` — label text provides the accessible name.
- Category headings are `<p>` elements (no ARIA role); they precede their group but are not explicitly linked to the group via `aria-labelledby`. Screen readers navigate by `role="option"` within the `listbox`.
- The `aria-selected` attribute on the active `option` communicates keyboard focus to screen readers.

---

## 4. Known gaps

| Gap | Severity | Description |
|---|---|---|
| `aria-activedescendant` not set | Medium | The `combobox` input does not set `aria-activedescendant` to the id of the active option. Screen readers may not announce the active option as the user arrows through results. |
| Result `<button>` ids not set | Medium | Options have no `id` attribute; `aria-activedescendant` cannot reference them without ids. |
| Category headings not associated | Low | `<p>` category headings are not linked to their option group via `aria-labelledby` on a `group` role. |
| No live region for result count | Low | No `aria-live` region announces how many results matched; screen readers do not hear "5 results found". |
| `type="search"` semantics | Informational | `<input type="search">` renders a clear button in some browsers; clearing via the browser clear button does not fire the React `onChange` in all implementations. |
