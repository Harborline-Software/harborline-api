# PhoneField — Accessibility Contract

- **Component:** PhoneField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PhoneField.Semantic.md) · [Interaction](./PhoneField.Interaction.md) · [Accessibility](./PhoneField.Accessibility.md) · [Styling](./PhoneField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PhoneField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PhoneField renders a native `<input type="tel">` with a leading country code
adornment. This contract names label, error, hint, and ARIA attributes.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<input type="tel">` | implicit `textbox` | Native `type="tel"` input |
| Label `<label>` (optional) | (linked) | Visible; `htmlFor={id}` |
| `+1` prefix `<span>` | (decorative) | No ARIA role; `select-none` |
| Hint `<p>` | (none) | `id="{id}-hint"` |
| Error `<p>` | `role="alert"` | Live region |

---

## 3. `type="tel"`

Using `type="tel"` opens the phone number keypad on mobile and signals input
purpose to AT.

**WCAG citation:** WCAG 2.2 SC 1.3.5 Identify Input Purpose.

---

## 4. `autocomplete`

No `autocomplete` attribute is set by default.

> **Gap A-1:** `autoComplete="tel"` or `"tel-national"` should be set for
> WCAG SC 1.3.5 compliance and browser autofill support. Hosts may spread it
> via `...props`.

---

## 5. Label linkage

When `label` is provided, `<label htmlFor={id}>` renders. AT announces label
on focus.

---

## 6. `aria-describedby`

`describedBy` is read from `FormFieldContext` (`ctx?.describedBy`), falling
back to `hint ? \`${id}-hint\` : undefined`. The resolved value is set on
`aria-describedby`.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 3.3.2.

---

## 7. Error state — `aria-invalid`

When `error` is truthy:

```tsx
aria-invalid={hasError || undefined}
```

`<p role="alert">` fires the error message as a live region.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 4.1.2.

---

## 8. Country code prefix `+1`

The `+1` span is a visual adornment with no `aria-label`. AT may read its text
content ("plus one") but this is generally harmless.

> **Gap A-2:** When AT users focus the input, they hear the label but not the
> `+1` prefix explicitly. Hosts should include "US phone" or "(+1)" in the
> FormField label for clarity.

---

## 9. Disabled state

Via prop spread: native `disabled` on the input.

---

## 10. Focus ring

```
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500
```

Error:

```
focus:ring-red-500 focus:border-red-500
```

`ring-2` on the input. Satisfies WCAG 2.4.7 and 2.4.13.

---

## 11. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | No `autocomplete` default | WCAG SC 1.3.5 | Add `autoComplete="tel-national"` |
| A-2 | `+1` prefix not AT-labeled | Advisory | Include in FormField label text |
