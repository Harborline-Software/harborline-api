# CurrencyField — Accessibility Contract

- **Component:** CurrencyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyField.Semantic.md) · [Interaction](./CurrencyField.Interaction.md) · [Accessibility](./CurrencyField.Accessibility.md) · [Styling](./CurrencyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CurrencyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CurrencyField wraps a native `<input type="text">` with locale-aware currency presentation. It
integrates with `FormFieldContext` for label and description linkage. This contract names the ARIA
surface and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<input type="text">` | implicit `textbox` | Native text input |
| Custom currency symbol `<span>` | (none — decorative) | Present only for an explicit `currencySymbol`; `pointer-events-none` |

---

## 3. Label-input linkage

The input's `id` is sourced from `useFormField()`. When inside a FormField,
the FormField's `<label htmlFor={id}>` links to this input, satisfying WCAG SC
1.3.1 and SC 4.1.2.

**Standalone usage.** The host MUST supply a label via:
- A sibling `<label for={id}>`, OR
- `aria-label` spread via `...props`, OR
- `aria-labelledby` spread via `...props`.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 4.1.2, SC 2.5.3.

---

## 4. `aria-describedby`

`describedBy` is read from `FormFieldContext` and set on the input. When
outside a FormField, the attribute is omitted.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 3.3.2.

---

## 5. Error state — `aria-invalid`

When `error === true`:

```tsx
aria-invalid={error ? true : undefined}
```

AT announces "invalid" on focus. Omitted (not `false`) when no error.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 4.1.2.

---

## 6. Currency presentation

The default ISO-4217 presentation is part of the input's displayed value, so assistive technology
can query the formatted currency. An explicit legacy/custom `currencySymbol` remains a decorative
span; hosts using that override SHOULD include the currency in the FormField label.

> **Gap A-1 (narrowed):** a custom `currencySymbol` adornment is not announced to AT. Include the
> currency code or name in the label when using this backwards-compatible override.

---

## 7. Disabled state

Native `disabled` attribute is set. AT announces as "unavailable". Removed
from tab order (native).

**WCAG citation:** WCAG 2.2 SC 4.1.2.

---

## 8. Input mode

`inputMode="decimal"` is set on the input. On mobile, this opens a decimal-
optimised keyboard. AT reads the field as a `textbox`.

---

## 9. Focus ring

```
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1
```

`ring-2` (2px) — satisfies WCAG 2.4.13 Focus Appearance minimum area
requirement for a standard-height input.

**WCAG citations:** WCAG 2.2 SC 2.4.7, SC 2.4.13.

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Currency symbol not exposed to AT | WCAG SC 1.3.1 | Include currency in the FormField label text |
| A-2 | No `autoComplete` for currency amount by default | WCAG SC 1.3.5 | Host may spread `autoComplete="transaction-amount"` |
