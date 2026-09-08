# DateTimePicker — Semantic Contract

- **Component:** DateTimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DateTimePicker.Interaction.md) · [Accessibility](./DateTimePicker.Accessibility.md) · [Styling](./DateTimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateTimePicker.tsx`
- **Catalog row:** #40 DateTimePicker (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — composes DatePicker + TimePicker (both hand-rolled)

---

## 1. Component purpose

**DateTimePicker** — a date and time combined input wrapping `<input type="datetime-local">`. Extends DatePickerProps with optional time format and step configuration. Emits `Date | null`.

---

## 2. Props

```typescript
interface DateTimePickerProps extends DatePickerProps {
  timeFormat?: string           // reserved; not used in M1
  steps?: {
    hour?: number
    minute?: number
    second?: number
  }                             // reserved; not used in M1
}

// From DatePickerProps (relevant inherited props):
//   value?: Date | null
//   defaultValue?: Date | null
//   onValueChange?: (date: Date | null) => void
//   disabled?: boolean
//   className?: string
```

---

## 3. Value format

Converts `Date | null` ↔ `"YYYY-MM-DDTHH:mm"` string for the native input. On `onChange`, parses via `new Date(e.target.value)` (or `null` for empty).

---

## 4. Visual variants

In M1, DateTimePicker has **no size/fillMode/rounded variants** — hardcoded to:
- `h-9 text-sm px-3 bg-white border border-input rounded-md`

This differs from TimePicker which exposes full size/fillMode/rounded props.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required), FR-2 (open/onOpenChange + onFocus/onBlur), FR-3 (size/fillMode/rounded — resolves G-DTP1).
> Supersedes: G-DTP1 (no size/fillMode/rounded — REQUIRED at Wave-N), G-DTP3 (no id prop — REQUIRED at Wave-N).

### 5. Additional props

```typescript
// Added in Wave-N
interface DateTimePickerPropsExpansion {
  // FR-3: appearance axes (resolves G-DTP1)
  size?: 'sm' | 'md' | 'lg'                     // default: 'md'
  fillMode?: 'solid' | 'outline' | 'flat'        // default: 'solid'
  rounded?: 'none' | 'sm' | 'md' | 'lg' | 'full' // default: 'md'

  // Resolves G-DTP3: id for <label htmlFor>
  id?: string
  name?: string

  // FR-2: popup control
  open?: boolean
  defaultOpen?: boolean
  onOpenChange?: (open: boolean) => void

  // FR-2: focus events
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // Time-range constraints (resolves audit minTime/maxTime P1)
  minTime?: Date | string    // minimum selectable time (time component only)
  maxTime?: Date | string    // maximum selectable time (time component only)

  // steps now functional (resolves G-DTP2 — promoted from reserved to functional)
  // steps inherited from DateTimePickerProps; hour/minute/second increments

  // FR-1
  required?: boolean
  error?: boolean

  // ARIA
  ariaDescribedBy?: string
  ariaLabelledBy?: string
  ariaLabel?: string
}
```

### 6. size / fillMode / rounded (FR-3 — resolves G-DTP1)

`size`, `fillMode`, and `rounded` are now functional. The Styling contract Wave-N §7 documents the class recipes. These bring DateTimePicker into parity with TimePicker.

### 7. id / name (resolves G-DTP3)

`id` is forwarded to the underlying input element enabling `<label htmlFor={id}>` association. This resolves G-DTP3. Both `id` and `name` are required for accessible form integration.

### 8. open / onOpenChange (FR-2)

`open` / `onOpenChange` provide controlled popup visibility. The native `<input type="datetime-local">` has no popup API, so this prop controls a future custom picker panel. In the M1 native-input path, `open` / `onOpenChange` are no-ops (the native input manages its own picker). They are carried as contract surface now.

### 9. minTime / maxTime

`minTime` and `maxTime` constrain the time component independently of `min`/`max` (which constrain the date component). In the M1 native-input path, these are passed as the time portion of the native `min`/`max` attributes when no date `min`/`max` is set, and are no-ops otherwise. The custom picker panel wave implements full time-axis clamping.

### 10. steps (resolves G-DTP2 — promoted from reserved)

`steps.hour`, `steps.minute`, `steps.second` are now functional contract surface. In the native-input path they map to the `step` attribute (minute × 60 + second). In the custom picker panel wave, they control the increment per scroll/arrow-key in each time segment.
