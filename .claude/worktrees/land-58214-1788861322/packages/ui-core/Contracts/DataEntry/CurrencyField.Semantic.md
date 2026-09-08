# CurrencyField — Semantic Contract

- **Component:** CurrencyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CurrencyField.Interaction.md) · [Accessibility](./CurrencyField.Accessibility.md) · [Styling](./CurrencyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CurrencyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — wraps NumericTextBox (hand-rolled)

---

## 1. Purpose

CurrencyField is a controlled text input for entering monetary amounts. At rest it presents the
amount with the `HarborlineLocaleProvider` number-format seam and an ISO-4217 currency code (default
`USD`); while editing it exposes the invariant numeric string owned by the host. It integrates with
`FormFieldContext` for label linkage and `aria-describedby`. HTML attribute passthrough is supported
via spread props.

---

## 2. Data model

CurrencyField is fully controlled. The host owns the string value; `onChange`
delivers the cleaned string on each keystroke; `onBlur` normalizes the value
to 2 decimal places.

```typescript
interface CurrencyFieldProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'id' | 'value' | 'onChange'> {
  name: string
  value: string           // raw string value — e.g. "1234.5" or "1234.50"
  onChange: (value: string) => void
  currency?: string       // ISO-4217 code; default "USD"
  currencySymbol?: string // explicit legacy/custom adornment override
  locale?: string         // optional BCP-47 display override; provider locale by default
  disabled?: boolean
  error?: boolean
  placeholder?: string    // default "0.00"
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `name` | `string` | required | The field `name` attribute on the underlying `<input>`. |
| `value` | `string` | required | Controlled value. Stored as a numeric string. |
| `onChange` | `(value: string) => void` | required | Fires on every change event with the cleaned string (only digits, `.`, `-` allowed). |
| `currency` | `string` | `'USD'` | ISO-4217 code passed to the locale number-format seam for at-rest display. |
| `currencySymbol` | `string` | — | Explicit custom/legacy leading adornment. When supplied, it wins over `currency` presentation and the seam formats only the numeric portion. |
| `locale` | `string` | provider locale | Optional BCP-47 display override passed through the same formatter seam. |
| `disabled` | `boolean` | `false` | Disables the input. |
| `error` | `boolean` | `false` | Enables error visual treatment and sets `aria-invalid={true}`. |
| `placeholder` | `string` | `'0.00'` | Shown when `value` is empty. |
| `...props` | `React.InputHTMLAttributes` | — | Additional HTML input attributes spread onto the native `<input>` (except `type`, `id`, `value`, `onChange`). This includes `onBlur`, `onFocus`, `autoComplete`, `maxLength`, etc. |

### 3.1 `id` from FormFieldContext

The input's `id` is read from `useFormField()` (the `id` field). This links the
input to the parent FormField's `<label htmlFor={...}>`. When used outside a
FormField, the context returns an empty string and `id` is effectively absent.

### 3.2 `aria-describedby` from FormFieldContext

`describedBy` is read from `useFormField()` and set on `aria-describedby`.
When outside a FormField, the attribute is omitted.

### 3.3 Display formatting and blur normalization

The controlled `value` remains an invariant numeric string. While focused, the input displays that
raw string so caret movement and editing are predictable. While unfocused, a valid numeric value is
displayed through `formatNumber`:

- normal path: `{ style: 'currency', currency }`, which gives ISO-4217 fraction digits, symbol/code,
  grouping, decimal marks, digit shapes, and currency placement for the effective locale;
- explicit `currencySymbol` path: `{ minimumFractionDigits: 2, maximumFractionDigits: 2 }`, with the
  supplied string retained as the leading legacy/custom adornment.

On blur, `parseFloat(value)` is attempted. If valid, the host's stored value is still normalized to
`num.toFixed(2)`. This invariant storage behavior is deliberately separate from locale-aware
presentation and preserves the zero-config callback contract. If parsing fails, no replacement is
made.

The host's `onBlur` (if spread via `...props`) is invoked after normalization.

While focused, the default currency path intentionally shows only the invariant numeric string,
without a currency symbol or adornment. This preserves predictable selection, caret movement, and
editing; the locale-aware currency presentation returns on blur.

With no provider or formatting callback, the default `en` + `USD` path displays the same dollar and
two-decimal semantics consumers previously received from the literal `$` plus `toFixed(2)` behavior.

### 3.4 Input type and mode

The input renders as `type="text"` with `inputMode="decimal"`, which opens a
decimal-optimised keyboard on mobile without native browser number input
behaviour (no spin buttons, no native max/min enforcement).

### 3.5 Value cleaning

`onChange` strips all characters except digits (`0-9`), `.`, and `-` before
calling the host callback. This means the host always receives a clean numeric
string, not raw user input.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(value: string)` | Every keystroke; delivers cleaned string (digits, `.`, `-` only). |
| (blur) | via `...props` spread | `onBlur` from the spread fires after the normalization side effect. |

---

## 5. Composition

- **FormField (canonical).** Wrap in a FormField to get label, hint, and error
  message. The `name` prop drives `htmlFor` linkage; `useFormField()` provides
  `id` and `describedBy`.
- **Standalone.** Permitted; add a sibling `<label>` or `aria-label` manually.
- **HTML passthrough.** Additional attributes (e.g. `autoComplete="transaction-amount"`, `maxLength`, `data-*`) can be spread via `...props`.

---

## 6. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | No `size` prop — single `md`-equivalent density |
| S-3 | No negative value guard — the `-` character is allowed in the raw string |
| S-4 | No `min` / `max` enforcement — range validation is host responsibility |

S-2 was closed by the 2026-07-16 locale-format-seam amendment. `toFixed(2)` now describes invariant
host storage only; visible at-rest presentation is locale- and ISO-4217-aware.
