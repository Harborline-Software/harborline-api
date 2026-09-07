# InlineEdit — Accessibility Contract

- **Component:** InlineEdit
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./InlineEdit.Semantic.md) · [Interaction](./InlineEdit.Interaction.md) · [Accessibility](./InlineEdit.Accessibility.md) · [Styling](./InlineEdit.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/InlineEdit.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

InlineEdit exposes two AT surfaces depending on mode: a `<button>` in view
mode and an `<input>` in edit mode. The `aria-label` prop threads a meaningful
label to both. Mode transitions preserve keyboard focus via `autoFocus`.

---

## 2. ARIA structural roles

| Mode | Element | Role | Notes |
|---|---|---|---|
| View | `<button type="button">` | `button` | Accessible name from `aria-label` or `"Click to edit"` |
| Edit | `<input type="text">` | `textbox` | Accessible name from `aria-label` |
| Pencil icon SVG | decorative | `aria-hidden="true"` | Shown on hover/focus-visible |

---

## 3. View mode button label

```tsx
aria-label={ariaLabel ? `Edit ${ariaLabel}` : 'Click to edit'}
```

When `aria-label` is provided (e.g. `"item name"`), the button is announced
as "Edit item name, button". When omitted, announced as "Click to edit, button".
Hosts SHOULD supply `aria-label` for context — "Click to edit" is generic.

**WCAG citations:**
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 2.5.3 Label in Name

---

## 4. Edit mode input label

```tsx
aria-label={ariaLabel}
```

When `ariaLabel` is provided, the input has that accessible name. When omitted,
the input has no `aria-label` — it is unlabelled (known gap G1).

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 5. Focus management

- **View → Edit:** the edit `<input>` has `autoFocus`. AT announces the input
  and its value when focus moves.
- **Edit → View (confirm or cancel):** `editing = false`, the view `<button>`
  re-renders. Focus is NOT explicitly returned to the button — the browser
  may move focus to the document body or to the next focusable element (known
  gap G2).

**WCAG citations:**
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 3.2.1 On Focus

---

## 6. Focus visible

View button: `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`
Edit input: `focus:outline-none focus:ring-2 focus:ring-blue-500`

Both use `ring-2` — meets WCAG 2.4.13 Focus Appearance.

**WCAG citations:**
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.4.13 Focus Appearance

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Edit input has no `aria-label` when `ariaLabel` prop is omitted | High | Require `aria-label` prop; or derive a default from the placeholder or surrounding context |
| G2 | Focus is not returned to the view button after editing — may jump to body | High | Call `buttonRef.current?.focus()` on `editing → false` transition |
