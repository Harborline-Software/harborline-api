# CreditCardField — Semantic Contract

- **Component:** CreditCardField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CreditCardField.Interaction.md) · [Accessibility](./CreditCardField.Accessibility.md) · [Styling](./CreditCardField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CreditCardField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled card input group

---

## 1. Purpose

CreditCardField is a composite form section for collecting payment card data. It
renders four input fields — card number, name on card, expiry (MM/YY), and CVC
— in a single controlled component. It handles display formatting (card number
chunking, expiry slash insertion) and auto-detects the card network (Visa,
Mastercard, Amex, Discover) from the card number prefix, adjusting field
constraints accordingly.

This component does NOT submit data or validate card numbers using Luhn
algorithm — that is the host's responsibility.

---

## 2. Data model

CreditCardField is fully controlled via a single value object.

```typescript
type CardNetwork = 'visa' | 'mastercard' | 'amex' | 'discover' | 'unknown'

interface CreditCardValue {
  number: string    // raw digits only (no spaces/dashes) — e.g. "4111111111111111"
  name: string      // cardholder name as typed
  expiry: string    // raw digits only — "MMYY" e.g. "1227" for Dec 2027
  cvc: string       // raw digits only — 3 or 4 depending on network
}

interface CreditCardFieldProps {
  value: CreditCardValue
  onChange: (value: CreditCardValue) => void
  disabled?: boolean
  required?: boolean
  error?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `CreditCardValue` | required | The full controlled state object. All four sub-fields are controlled. |
| `onChange` | `(value: CreditCardValue) => void` | required | Fires whenever any sub-field changes. The payload always replaces the full `CreditCardValue` object (immutable update). |
| `disabled` | `boolean` | `false` | When `true`, all four inputs are non-interactive. |
| `required` | `boolean` | `false` | Canonical FR-1 required state. Applies native `required` to all four inputs; combines with `FormFieldContext.required` via logical OR. |
| `error` | `boolean` | `false` | Canonical FR-1 composite invalid flag. Applies `aria-invalid` and error chrome to all four inputs. |
| `className` | `string` | `''` | Merged onto the root `<div>`. |

### 3.1 Value shape — stored vs. displayed

The `value` object holds **raw digits** (no formatting characters). The
component applies display formatting on the fly:

| Field | Raw storage | Display |
|---|---|---|
| `number` | `"4111111111111111"` | `"4111 1111 1111 1111"` (standard) or `"3782 822463 10005"` (Amex 4-6-5) |
| `expiry` | `"1227"` | `"12/27"` |
| `cvc` | `"123"` | `"123"` (stored same as displayed) |
| `name` | `"Jane Smith"` | `"Jane Smith"` (passthrough) |

### 3.2 Network detection

Card network is derived from the `number` field's prefix at render time. It
drives:

- The `maxLength` on the card number input (19 for standard 16-digit; 17 for
  Amex 15-digit, accounting for 2 spaces in Amex 4-6-5 format).
- The raw digit cap on `number` (16 for standard, 15 for Amex).
- The `maxLength` and placeholder on the CVC input (3 for standard, 4 for Amex).
- The network badge label displayed in the card number field (`"Visa"`,
  `"Mastercard"`, `"Amex"`, `"Discover"`, or nothing for `'unknown'`).

### 3.3 Hardcoded sub-field IDs

The sub-field inputs use hardcoded HTML `id` attributes: `cc-number`,
`cc-name`, `cc-expiry`, `cc-cvc`. This means CreditCardField cannot be used
multiple times on the same page without id conflicts.

> **Gap S-1:** Hardcoded sub-field `id` values (`cc-number`, `cc-name`,
> `cc-expiry`, `cc-cvc`). Multiple instances on the same page cause duplicate
> ids. A future fix should prefix ids from an `id` prop or `useId()`.

### 3.4 autocomplete attributes

All four inputs carry the relevant `autocomplete` values:

| Input | `autocomplete` |
|---|---|
| Card number | `cc-number` |
| Name | `cc-name` |
| Expiry | `cc-exp` |
| CVC | `cc-csc` |

This enables browser/password-manager autofill for payment forms.

### 3.5 FormField integration and validation

CreditCardField renders its own `<label>` elements internally (one per
sub-field). It consumes `FormFieldContext` for `required`, `disabled`, and
`describedBy`. Context and local required/disabled values combine via logical
OR. The `describedBy` token is applied to every sub-input so a composed
FormField/ValidationMessage announcement is available from each focus target.

Per binding family ruling FR-1, `error?: boolean` is the invalid flag and
validation messages remain composed. No `valid` or per-component
`validationMessage` prop is added. Hosts resolve stable validation codes
through the active catalog before passing the resulting text to
FormField/ValidationMessage; English literals are not a validation transport.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `CreditCardValue` | Any sub-field changes. The raw digit value is stored; the formatted display value is computed at render. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | Hardcoded sub-field ids — multiple instances break label linkage |
| S-3 | No Luhn validation — card number validity is host responsibility |
| S-4 | No `onBlur` / `onFocus` event surface on sub-fields |
| S-5 | No `size` variant — single fixed density |
