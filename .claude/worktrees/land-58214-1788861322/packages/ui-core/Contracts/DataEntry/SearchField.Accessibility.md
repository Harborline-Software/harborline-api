# SearchField — Accessibility Contract

- **Component:** SearchField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchField.Semantic.md) · [Interaction](./SearchField.Interaction.md) · [Accessibility](./SearchField.Accessibility.md) · [Styling](./SearchField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

SearchField uses `<input type="search">` for its semantic role, integrates with
`FormFieldContext` for id and `aria-describedby`, and exposes `aria-invalid`.
The leading search icon is decorative; the clear button has an `aria-label`.

---

## 2. ARIA structural roles

| Element | Implicit role | Notes |
|---|---|---|
| `<input type="search">` | `searchbox` | Native search type |
| Leading search SVG | decorative | `aria-hidden="true"` |
| Clear `<button>` | `button` | `aria-label="Clear search"` |
| Clear button SVG | decorative | `aria-hidden="true"` |

---

## 3. `<input type="search">` semantics

The `type="search"` hint:
- AT announces this as a "search" field.
- Some browsers show a native clear (✕) button. SearchField adds its own
  custom clear button — this may result in two clear buttons in some
  browsers (known gap G1).
- WCAG SC 1.3.5: the browser may associate `type="search"` as the input
  purpose, but there is no autocomplete token for search fields.

**WCAG citations:**
- WCAG 2.2 SC 1.3.5 Identify Input Purpose
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 4. FormField integration — id + describedBy

```typescript
const { id, describedBy } = useFormField()
```

```tsx
<input
  id={id}
  aria-describedby={describedBy}
  aria-invalid={error ? true : undefined}
  ...
/>
```

- `id` from context wires the FormField's `<label htmlFor>` to this input.
- `aria-describedby` from context wires the FormField's hint/error message.
- When used outside FormField, `id` and `describedBy` are undefined — the
  input has no id and no `aria-describedby`.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 3.3.2 Labels or Instructions

---

## 5. Error state

```tsx
aria-invalid={error ? true : undefined}
```

Error border changes to red. Both channels (visual border + `aria-invalid`)
are active when `error === true`.

**WCAG citations:**
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 6. Clear button

`aria-label="Clear search"`. Renders only when the field has a value.
AT users hear "Clear search, button" when they Tab to it.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 7. Focus visible

Input: `focus:ring-2 focus:ring-blue-500` — meets WCAG 2.4.13.
Clear button: `hover:text-gray-600` (no explicit focus ring visible on button — known gap G2).

---

## 8. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | `type="search"` + custom clear button may result in double clear button in some browsers | Low | Add `-webkit-appearance:none` or detect browser native clear button |
| G2 | Clear button has no visible focus ring | High | Add `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500` to clear button |
| G3 | No live region for search results count | Low | Host should provide `aria-live="polite"` result count |
