# PhoneField — Semantic Contract

- **Component:** PhoneField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PhoneField.Interaction.md) · [Accessibility](./PhoneField.Accessibility.md) · [Styling](./PhoneField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PhoneField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled phone number input

---

## 1. Purpose

PhoneField is a US phone number entry field. It formats digits as the user
types using the `(NXX) NXX-XXXX` pattern (NANP format) and exposes both the
raw digit string and the formatted string via `onValueChange`. It uses
**semi-controlled** state: internal `displayValue` drives the visible input,
while the host receives raw digits via the callback.

---

## 2. Data model

PhoneField is semi-controlled. Internal state holds the formatted display
value. The host receives raw digits and formatted string via `onValueChange`.

```typescript
interface PhoneFieldProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange'> {
  label?: string
  error?: string        // error message; shows <p role="alert">
  hint?: string         // hint text; shown when no error
  onValueChange?: (raw: string, formatted: string) => void
}
```

Note: `value` and `defaultValue` from `React.InputHTMLAttributes` ARE included
in the spread. They initialize `displayValue` via `formatPhone(String(props.value ?? props.defaultValue ?? ''))` at mount. Subsequent `value` prop changes do NOT update `displayValue` — see gap S-1.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `label` | `string` | `undefined` | Visible label above the input. |
| `error` | `string` | `undefined` | Error message. Renders `<p role="alert">`. Hides hint. Sets `aria-invalid`. |
| `hint` | `string` | `undefined` | Hint linked via `aria-describedby` if no FormFieldContext `describedBy`. Shown only when no error. |
| `onValueChange` | `(raw: string, formatted: string) => void` | `undefined` | The primary change callback. Fires with raw digit string (max 10 digits) and formatted string. |
| `id` | `string` | `ctx?.id` | Input id from FormFieldContext or prop. |
| `...props` | `React.InputHTMLAttributes` | — | Spread onto the `<input>` (except `type` and `onChange`). Includes `disabled`. |

### 3.1 US-only format

PhoneField is hardcoded to US (+1) NANP format: `(NXX) NXX-XXXX`. The `+1`
country code prefix is rendered as a non-interactive leading adornment. No
international phone number support.

### 3.2 Raw digit storage

The host receives only the raw digit string (max 10 characters, no `+1` prefix)
via `onValueChange`. The formatted string is also provided for display or
copy-to-clipboard use cases.

### 3.3 FormField integration

Reads `ctx?.id` and `ctx?.describedBy` from `FormFieldContext`. `describedBy`
from context takes precedence over the internally-computed hint-based
`aria-describedby`.

### 3.4 Not fully controlled

PhoneField does NOT sync the display value to `value` prop changes after mount.
If the host updates `value` externally, the display does not update.

> **Gap S-1:** Semi-controlled — initial value used to seed `displayValue` but
> subsequent `value` prop changes are ignored. Fix: use a fully controlled
> pattern with `value` → `formatPhone(value)` computed at render.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onValueChange` | `(raw: string, formatted: string)` | Every input change; `raw` is max-10 digits, `formatted` is `(NXX) NXX-XXXX` style. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | Semi-controlled — `value` prop changes after mount don't update display |
| S-2 | US-only — no international phone number support |
| S-3 | No `size` prop |
| S-4 | No explicit `onBlur` event (passthrough via `...props` only) |
