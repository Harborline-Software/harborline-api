# PropertySelector — Accessibility Contract

- **Component:** PropertySelector
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted (domain-relocated to Harborline App; NOT `@harborline-software/ui-react`)
- **Companion contracts:** [Semantic](./PropertySelector.Semantic.md) · [Interaction](./PropertySelector.Interaction.md) · [Accessibility](./PropertySelector.Accessibility.md) · [Styling](./PropertySelector.Styling.md)
- **Reference implementation:** the Harborline App's `src/property/PropertySelector.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PropertySelector implements a combobox pattern with `role="combobox"` on the
trigger and `role="listbox"` + `role="option"` on the dropdown. The WAI-ARIA
combobox pattern requires keyboard navigation in the listbox; M1 is missing
this (gap G1).

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Trigger `<button>` | `combobox` | `aria-expanded`, `aria-haspopup="listbox"` |
| Dropdown `<ul>` | `listbox` | Implicit via `role="listbox"` |
| Option `<li>` | `option` | `aria-selected={prop.id === value}` |
| Search `<input>` | `textbox` | `aria-label="Search properties"` |
| Chevron SVG | decorative | No `aria-hidden` — known gap G2 |
| Property icon `<div>` | decorative | No `aria-hidden` — known gap G2 |

---

## 3. Trigger combobox attributes

```tsx
role="combobox"
aria-expanded={open}
aria-haspopup="listbox"
disabled={disabled}
```

`aria-controls` pointing to the listbox `id` is absent — known gap G3.
The listbox also has no `id`.

---

## 4. Label association

When `label` is provided, a `<label htmlFor={inputId}>` renders. `inputId` is
`id` prop or `useId()` fallback. The trigger `<button>` has `id={inputId}`. The
`<label htmlFor>` ↔ `<button id>` linkage is non-standard — `htmlFor` targets
form controls (inputs), not buttons. AT may not announce the label as the
button's accessible name.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 5. Selected option indication

`aria-selected={prop.id === value}` is set on each `<li role="option">`. AT
users can hear which option is currently selected when they navigate into the
listbox — but since there is no keyboard navigation, AT users cannot reach the
listbox options in M1.

---

## 6. Focus management

When the dropdown opens, focus moves to the search input (via `setTimeout(0)`,
see Interaction §2). When the dropdown closes (selection or outside click),
focus is NOT returned to the trigger button.

**WCAG citation:** WCAG 2.2 SC 2.4.3 Focus Order.

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | No keyboard navigation in listbox — arrow keys not handled | Critical | Implement WAI-ARIA combobox pattern: Arrow Down/Up to navigate options, Enter to select, Escape to close |
| G2 | Chevron SVG and property icon `<div>` lack `aria-hidden` | Low | Add `aria-hidden="true"` to both |
| G3 | `aria-controls` missing on trigger — not linked to listbox | Medium | Add `id` to listbox `<ul>` and `aria-controls={listId}` on trigger |
| G4 | `<label htmlFor={buttonId}>` targets a button, not an input — AT may not associate | Medium | Use `aria-labelledby` on the trigger or `aria-label` instead of `htmlFor` |
| G5 | Focus not returned to trigger on close | High | Call `triggerRef.current?.focus()` on dropdown close |
