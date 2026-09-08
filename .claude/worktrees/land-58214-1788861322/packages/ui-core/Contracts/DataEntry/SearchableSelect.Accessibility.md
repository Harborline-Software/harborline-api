# SearchableSelect — Accessibility Contract

- **Component:** SearchableSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchableSelect.Semantic.md) · [Interaction](./SearchableSelect.Interaction.md) · [Accessibility](./SearchableSelect.Accessibility.md) · [Styling](./SearchableSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchableSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

SearchableSelect implements the WAI-ARIA combobox pattern partially. The
structural ARIA attributes (`role="combobox"`, `aria-expanded`,
`aria-haspopup`, `aria-controls`) are present. Option `aria-selected` is
wired. Keyboard navigation in the listbox is absent.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Trigger `<button>` | `combobox` | `aria-expanded`, `aria-haspopup="listbox"`, `aria-controls={listId}`, `aria-required` |
| Dropdown `<ul>` | `listbox` | `id={listId}`, `aria-label={label ?? 'Options'}` |
| Option `<li>` | `option` | `aria-selected={opt.value === value}` |
| Group header `<li>` | `presentation` | Decorative group label |
| Search `<input>` | `textbox` | `aria-label="Search options"` |
| Chevron SVG | decorative | No `aria-hidden` — known gap G1 |

---

## 3. Trigger combobox attributes

```tsx
role="combobox"
aria-expanded={open}
aria-haspopup="listbox"
aria-controls={listId}
aria-required={required}
```

This is the most complete combobox ARIA wiring in the DataEntry family. The
`aria-controls` correctly points to the listbox `id`.

---

## 4. Label association

`<label htmlFor={inputId}>` where `inputId` is the trigger button's id. Same
caveat as PropertySelector: `htmlFor` targeting a button is non-standard. AT
may not announce the label as the trigger's accessible name.

`required` renders a `<span aria-hidden="true">*</span>` — the `aria-hidden`
means the asterisk is not read by AT. The `aria-required` on the trigger is
the programmatic signal.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 3.3.2 Labels or Instructions
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 5. Error announcement

```tsx
<p className="text-xs text-red-600" role="alert">{error}</p>
```

When `error` is provided, a `role="alert"` paragraph is rendered. AT announces
it assertively when it appears.

Trigger border changes to `border-red-500` when error is present (visual
channel). `aria-invalid` is NOT set on the trigger — known gap G2.

**WCAG citations:**
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 4.1.3 Status Messages

---

## 6. Selected option display

`aria-selected` is set on the matching `<li role="option">`. AT users can
identify the currently selected option if they navigate into the listbox —
but without keyboard navigation in the listbox, they cannot reach the options.

---

## 7. Focus visible

Trigger: `focus:ring-2 focus:ring-blue-500` — meets WCAG 2.4.13.
Search input: `focus:ring-2 focus:ring-blue-500`.

---

## 8. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Chevron SVG lacks `aria-hidden` | Low | Add `aria-hidden="true"` |
| G2 | `aria-invalid` not set on trigger when `error` is present | High | Add `aria-invalid={Boolean(error)}` to the trigger |
| G3 | No arrow-key navigation in listbox | Critical | Implement WAI-ARIA combobox list navigation |
| G4 | Focus not returned to trigger after close | High | Return focus on close |
| G5 | `htmlFor` targeting button is non-standard for label association | Medium | Use `aria-labelledby` on trigger pointing to label element |
