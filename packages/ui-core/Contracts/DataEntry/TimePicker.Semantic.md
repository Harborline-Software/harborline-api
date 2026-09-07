# TimePicker — Semantic Contract

- **Component:** TimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TimePicker.Interaction.md) · [Accessibility](./TimePicker.Accessibility.md) · [Styling](./TimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TimePicker.tsx`
- **Catalog row:** #137 TimePicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled time input (HH:MM)

---

## 1. Component purpose

**TimePicker** — a time selection input wrapping `<input type="time">`. Supports `Date | string | null` value interchange, min/max constraints, hour/minute step configuration, and the standard fillMode/size/rounded visual variants.

---

## 2. Props

```typescript
interface TimePickerProps {
  value?: Date | string | null    // controlled
  defaultValue?: Date | string    // uncontrolled seed
  onValueChange?: (time: Date | null) => void
  format?: string                 // reserved; not used in M1 rendering
  min?: Date | string             // minimum selectable time
  max?: Date | string             // maximum selectable time
  steps?: {
    hour?: number
    minute?: number
    second?: number
  }                               // reserved; not used in M1 rendering
  disabled?: boolean              // default: false
  size?: 'small' | 'medium' | 'large'          // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'      // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'medium'
  id?: string
  name?: string
  className?: string
}
```

---

## 3. Value interchange

Internally normalizes to `HH:mm` string for the native input:
- `Date` → `HH:mm` via `getHours/getMinutes`
- `string` → takes first 5 characters (ISO time or HH:mm)
- `null` / `undefined` → `''` (empty input)

`onValueChange` emits a `Date` object (today's date with the selected H:M, seconds zeroed) or `null` for empty.

---

## 4. Controlled vs uncontrolled

`value !== undefined` = controlled. `defaultValue` = uncontrolled seed.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required), FR-2 (open/onOpenChange + onFocus/onBlur), FR-3 (size vocabulary migration to 'sm'|'md'|'lg').
> Supersedes: G-TP1 (format/steps no-ops — steps promoted to contract-functional surface; format remains reserved). G-TP2 (native appearance variance) acknowledged; custom popup path addresses it.

### 5. Additional props

```typescript
// Added in Wave-N
interface TimePickerPropsExpansion {
  // FR-2: controlled popup (resolves audit show/defaultShow + onOpen/onClose)
  open?: boolean
  defaultOpen?: boolean
  onOpenChange?: (open: boolean) => void

  // FR-2: focus events (resolves audit onFocus/onBlur P1)
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // "Now" shortcut (resolves audit nowButton P1)
  nowButton?: boolean    // default: false — shows a "Now" button in the popup footer

  // FR-1
  required?: boolean
  error?: boolean

  // ARIA (resolves audit ariaLabelledBy + ariaDescribedBy)
  ariaLabelledBy?: string
  ariaDescribedBy?: string
  ariaLabel?: string

  // steps is already in the interface; promoted to functional (see §6)
  // format is already in the interface; remains reserved (see §7)

  // placeholder: not in M1 interface; added for parity
  placeholder?: string
  readonly?: boolean
}
```

### 6. steps — promoted to functional contract surface (resolves G-TP1 partially)

`steps.hour`, `steps.minute`, `steps.second` are now functional contract surface. In the native-input path: the HTML `step` attribute on `<input type="time">` receives `(steps.minute ?? 1) * 60 + (steps.second ?? 0)` seconds. `steps.hour` is not natively supported and is a no-op in the native path. In the custom popup panel wave, all three axes control scroll/arrow-key increments in the time column lists.

### 7. nowButton

When `nowButton=true`: a "Now" button appears in the popup footer (custom popup path) or as a standalone button beside the native input (native path). Clicking it calls `onValueChange(new Date())` — the current local time.

### 8. open / onOpenChange (FR-2)

`open` / `onOpenChange` provide controlled popup visibility. In the native-input path, these are no-ops (native browser picker manages its own UI). In the custom popup panel path, `onOpenChange(true)` opens the clock/column popup; `onOpenChange(false)` closes it. `Escape` and outside-pointer-down fire `onOpenChange(false)`.

### 9. required / error (FR-1)

`required` → `aria-required` on the input. `error=true` → `aria-invalid` on the input. Validation messages composed externally via FormField / ValidationMessage.
