# NumberFormatField — Semantic Contract

- **Component:** NumberFormatField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumberFormatField.Interaction.md) · [Accessibility](./NumberFormatField.Accessibility.md) · [Styling](./NumberFormatField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberFormatField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled number format input

---

## 1. Purpose

NumberFormatField is a text input for entering numeric values that are
displayed in a formatted representation when not focused. When focused, the
raw numeric string is shown for editing. On blur, `Intl.NumberFormat` formats
the value. It supports optional prefix and suffix adornments and integrates
with `FormFieldContext`.

---

## 2. Data model

NumberFormatField is fully controlled. The host stores the raw numeric string;
the component handles format/unformat on focus/blur.

```typescript
interface NumberFormatFieldProps {
  value: string                    // raw numeric string — e.g. "1234.56"
  onChange: (raw: string) => void  // delivers cleaned raw string (digits, '.', '-' only)
  format?: Intl.NumberFormatOptions // default: { maximumFractionDigits: 2, useGrouping: true }
  prefix?: string
  suffix?: string
  placeholder?: string
  disabled?: boolean
  required?: boolean
  error?: boolean
  label?: string                   // sr-only label; linked to input via id
  id?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | required | Raw numeric string. Empty string is valid (blank input). |
| `onChange` | `(raw: string) => void` | required | Fires with cleaned string (digits, `.`, `-` only). |
| `format` | `Intl.NumberFormatOptions` | `{ maximumFractionDigits: 2, useGrouping: true }` | Format applied when not focused. |
| `prefix` | `string` | `undefined` | Text rendered left of the input (e.g. `"$"`, `"USD"`). |
| `suffix` | `string` | `undefined` | Text rendered right of the input (e.g. `"%"`, `"kg"`). |
| `placeholder` | `string` | `undefined` | Placeholder when `value` is empty. |
| `disabled` | `boolean` | `false` | Disables the input. |
| `required` | `boolean` | `false` | Canonical FR-1 required state; combines with `FormFieldContext.required` and sets native `required`. |
| `error` | `boolean` | `false` | Canonical FR-1 invalid flag; sets `aria-invalid` and destructive wrapper chrome. |
| `label` | `string` | `undefined` | Rendered as `<label className="sr-only">`. |
| `id` | `string` | `useId()` / `ctx?.inputId` | The input's `id` attribute. |
| `className` | `string` | `''` | Merged onto the wrapper element. |

### 3.1 Focus / blur value swap

The component maintains a `focused` boolean in local state. When not focused
and `value` is non-empty, the display value is `applyFormat(value, format)`.
When focused, the raw `value` string is shown directly. This lets the user edit
the raw number without fighting comma/grouping characters.

### 3.2 Value cleaning on change

On input, `e.target.value.replace(/[^0-9.-]/g, '')` is applied before calling
`onChange`. The host always receives a clean numeric string.

### 3.3 FormField integration

The `id` is resolved from
`idProp ?? ctx?.inputId ?? ctx?.id ?? genId`. The `label` prop
produces a `<label className="sr-only">` linked to the input. The component
combines local `required`/`disabled` with FormFieldContext via logical OR and
wires the context's `describedBy` to the input.

### 3.4 Validation-message composition

Per family ruling FR-1, `error?: boolean` is the invalid flag. Validation
messages remain in FormField/ValidationMessage/ValidationSummary; no parallel
`valid` or per-component `validationMessage` prop is added. Hosts resolve
stable validation codes through the active locale catalog before composing
the message.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(raw: string)` | Every input event; delivers cleaned raw string. |

Focus and blur events are handled internally (to swap display value) but are
not exposed to the host.

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-3 | No `onBlur` / `onFocus` surface for the host |
| S-4 | Format locale is hardcoded to `'en-US'` in `applyFormat` |
| S-5 | `format` errors are silently swallowed (try/catch returns raw) |
