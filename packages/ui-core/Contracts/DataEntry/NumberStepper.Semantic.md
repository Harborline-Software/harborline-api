# NumberStepper — Semantic Contract

- **Component:** NumberStepper
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumberStepper.Interaction.md) · [Accessibility](./NumberStepper.Accessibility.md) · [Styling](./NumberStepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberStepper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled increment/decrement control

---

## 1. Purpose

NumberStepper is a compound control for incrementing or decrementing a numeric
value via dedicated `−` and `+` buttons flanking a numeric input. It supports
`min`, `max`, and `step` constraints, three size variants, and disables the
decrement/increment buttons at the boundaries.

---

## 2. Data model

NumberStepper is fully controlled.

```typescript
type NumberStepperSize = 'sm' | 'md' | 'lg'

interface NumberStepperProps {
  value: number
  onChange: (value: number) => void
  min?: number          // default -Infinity
  max?: number          // default Infinity
  step?: number         // default 1
  size?: NumberStepperSize  // default 'md'
  disabled?: boolean
  label?: string        // sr-only label
  id?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `number` | required | Current numeric value. |
| `onChange` | `(value: number) => void` | required | Fires with the new clamped value after any change. |
| `min` | `number` | `-Infinity` | Lower bound. Decrement button is disabled when `value <= min`. Finite `min` is passed to native `<input min>`. |
| `max` | `number` | `Infinity` | Upper bound. Increment button is disabled when `value >= max`. Finite `max` is passed to native `<input max>`. |
| `step` | `number` | `1` | Step size for increment/decrement and native input step. |
| `size` | `NumberStepperSize` | `'md'` | Controls button and input heights. |
| `disabled` | `boolean` | `false` | When `true`, all three controls (decrement button, input, increment button) are disabled. |
| `label` | `string` | `undefined` | Renders a `<label className="sr-only">` linked to the input. |
| `id` | `string` | `useId()` / `ctx?.inputId` | The input's `id`. |
| `className` | `string` | `''` | Merged onto the root inline-flex container. |

### 3.1 Clamping

`clamp(v) = Math.min(max, Math.max(min, v))`. Applied on increment, decrement,
and manual input change. Ensures `value` never exceeds bounds.

### 3.2 Boundary disabling

- Decrement button: disabled when `disabled || value <= min`.
- Increment button: disabled when `disabled || value >= max`.

Infinite bounds (`-Infinity` / `Infinity`) mean the buttons are never disabled
by range alone.

### 3.3 Size variants

Three size classes control button and input dimensions:

| Size | Button classes | Input classes |
|---|---|---|
| `'sm'` | `h-7 w-7 text-sm` | `h-7 w-12 text-sm` |
| `'md'` | `h-9 w-9 text-base` | `h-9 w-14 text-base` |
| `'lg'` | `h-11 w-11 text-lg` | `h-11 w-16 text-lg` |

### 3.4 FormField integration

Reads `ctx?.inputId` from `useFormFieldContext()`. No `aria-describedby` wired.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(value: number)` | Decrement or increment button clicked; or manual input (non-NaN change event). Value is always clamped before firing. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | No `error` prop |
| S-2 | No `aria-describedby` from FormFieldContext |
| S-3 | No `onBlur` / `onFocus` surface |
| S-4 | `value` is not clamped on initial render — host may supply out-of-range initial values |
