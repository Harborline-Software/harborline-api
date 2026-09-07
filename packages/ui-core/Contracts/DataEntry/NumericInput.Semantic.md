# NumericInput — Semantic Contract

- **Component:** NumericInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumericInput.Interaction.md) · [Accessibility](./NumericInput.Accessibility.md) · [Styling](./NumericInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input type="number">`

---

## 1. Purpose

NumericInput is a self-contained numeric entry control that combines: an
optional visible label, an optional prefix adornment, decrement (`−`) and
increment (`+`) buttons inside a unified bordered wrapper, a numeric input,
an optional suffix adornment, an optional hint message, and an optional error
message. It renders its own label, hint, and error (not delegated to FormField).

---

## 2. Data model

NumericInput is fully controlled, accepting `number | ''` to support
an empty/blank state.

```typescript
interface NumericInputProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange'> {
  value?: number | ''
  onChange?: (value: number | '') => void
  min?: number
  max?: number
  step?: number         // default 1
  label?: string
  suffix?: string
  prefix?: string
  error?: string        // error message string; shows <p role="alert">
  hint?: string         // hint text; shown when no error
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `number \| ''` | `undefined` | Controlled value. `''` is the empty/blank state. |
| `onChange` | `(value: number \| '') => void` | `undefined` | Fires with the new value or `''`. |
| `min` | `number` | `undefined` | Lower bound. Decrement disables when `value <= min`. Native `min` attribute set. |
| `max` | `number` | `undefined` | Upper bound. Increment disables when `value >= max`. |
| `step` | `number` | `1` | Step for increment/decrement. |
| `label` | `string` | `undefined` | Visible label above the input. |
| `prefix` | `string` | `undefined` | Text badge on the left of the input (e.g. `"m²"`, `"from"`). |
| `suffix` | `string` | `undefined` | Text badge on the right of the input. |
| `error` | `string` | `undefined` | Error message; renders `<p role="alert">`. When set, `aria-invalid={true}` is set on the input. Hides `hint`. |
| `hint` | `string` | `undefined` | Hint text; rendered as `<p id="{id}-hint">` linked via `aria-describedby`. Suppressed when `error` is set. |
| `id` | `string` | `ctx?.id` | From FormFieldContext or passed directly. |
| `disabled` | `boolean` | native default | From spread props. |
| `...props` | `React.InputHTMLAttributes` | — | Spread onto the `<input>` (except `type` and `onChange`). |

### 3.1 Empty/blank state

`value === ''` is accepted and represents a blank input (user has cleared the
field or no value has been entered). `onChange('')` fires when the user clears
the input. This differs from `value === 0` which represents the numeric zero.

### 3.2 Error vs hint

When `error` is provided: the hint is hidden; an `<p role="alert">` with the
error string is rendered below the input.

When only `hint` is provided: an `<p id="{id}-hint">` is rendered; the input's
`aria-describedby` points to it.

### 3.3 FormField integration

Reads `ctx?.id` from `FormFieldContext`. Does NOT read `describedBy` from
context (manages its own hint/error description).

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(value: number \| '')` | Increment/decrement button clicked (if not at boundary); or input change event when parsed value is valid number or when input is blank/`-`. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | `aria-describedby` from FormFieldContext not wired — hint/error IDs managed internally |
| S-2 | No `size` prop |
| S-3 | Decrement/increment may overshoot due to float precision (uses `toPrecision(10)`) |
