# PercentageField — Semantic Contract

- **Component:** PercentageField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PercentageField.Interaction.md) · [Accessibility](./PercentageField.Accessibility.md) · [Styling](./PercentageField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PercentageField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — wraps NumericTextBox with `format='p2'`

---

## 1. Purpose

PercentageField is a self-contained numeric input for entering a percentage
value. It renders a unified bordered wrapper with a trailing `%` suffix
adornment. It enforces a configurable min/max range (defaults 0–100), renders
its own label, hint, and error message, and integrates with `FormFieldContext`
for the input `id`.

---

## 2. Data model

PercentageField is fully controlled, accepting `number | ''` to support
an empty/blank state.

```typescript
interface PercentageFieldProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange'> {
  value?: number | ''
  onChange?: (value: number | '') => void
  label?: string
  error?: string       // error message; shows <p role="alert">; hides hint
  hint?: string        // hint text; shown when no error
  min?: number         // default 0
  max?: number         // default 100
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `number \| ''` | `undefined` | Controlled value. `''` = blank. |
| `onChange` | `(value: number \| '') => void` | `undefined` | Fires with the clamped number or `''`. |
| `label` | `string` | `undefined` | Visible label above the input. |
| `error` | `string` | `undefined` | Error message. Renders `<p role="alert">`. Hides hint. Sets `aria-invalid`. |
| `hint` | `string` | `undefined` | Hint text linked via `aria-describedby`. Shown only when no error. |
| `min` | `number` | `0` | Lower bound. Values below min are clamped. |
| `max` | `number` | `100` | Upper bound. Values above max are clamped. |
| `id` | `string` | `ctx?.id` | Input id from FormFieldContext or prop. |
| `disabled` | `boolean` | native default | From spread props. |
| `...props` | `React.InputHTMLAttributes` | — | Spread onto the `<input>`. |

### 3.1 Clamping on change

When the user types a value:
- Empty string → `onChange('')`.
- Otherwise: `parseFloat(raw)` → if not NaN → `onChange(Math.min(max, Math.max(min, num)))`.

Clamping is applied on every change event. The user cannot type a value
outside [min, max].

### 3.2 Fixed step

The native `<input step>` is hardcoded to `0.1`, supporting tenths-of-a-percent
precision.

### 3.3 Suffix adornment

A `%` symbol is rendered as a right adornment (`border-l border-gray-200
bg-gray-50 px-3 py-2 text-sm text-gray-500`). It is purely visual.

### 3.4 FormField integration

Reads `ctx?.id` from `FormFieldContext`. Does NOT read `describedBy` — manages
its own hint ID scheme.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(number \| '')` | Every input change. Number values are clamped to [min, max]. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | `step` is hardcoded to `0.1` — no prop for custom precision |
| S-2 | No `size` prop |
| S-3 | `aria-describedby` from FormFieldContext not wired |
| S-4 | No decrement/increment buttons (unlike NumericInput) |
