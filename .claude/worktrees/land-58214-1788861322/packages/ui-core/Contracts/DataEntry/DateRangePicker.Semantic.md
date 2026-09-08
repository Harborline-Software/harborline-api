# DateRangePicker — Semantic Contract

- **Component:** DateRangePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DateRangePicker.Interaction.md) · [Accessibility](./DateRangePicker.Accessibility.md) · [Styling](./DateRangePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateRangePicker.tsx`
- **Catalog row:** #39 DateRangePicker (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled dual DateInput (no Radix primitive)

---

## 1. Component purpose

**DateRangePicker** — a range date selector with a trigger button that opens a side-by-side two-month calendar for start and end date selection. Displays the selected range as a formatted string in the trigger button.

---

## 2. Props

```typescript
interface DateRange {
  start: Date | null
  end: Date | null
}

interface DateRangePickerProps {
  value?: DateRange
  defaultValue?: DateRange
  onValueChange?: (range: DateRange) => void
  format?: string           // declared but not used
  min?: Date
  max?: Date
  disabledDates?: Date[] | ((date: Date) => boolean)  // not implemented in M1
  disabled?: boolean        // default: false
  size?: 'small' | 'medium' | 'large'             // declared but not applied
  fillMode?: 'solid' | 'outline' | 'flat'         // declared but not applied
  rounded?: 'small' | 'medium' | 'large' | 'full' // declared but not applied
  startPlaceholder?: string  // default: 'Start date'
  endPlaceholder?: string    // default: 'End date'
  className?: string
}
```

---

## 3. Controlled / uncontrolled

Controlled when `value` is provided. Internal `DateRange` state for uncontrolled. `onValueChange` fires on every click (including when end is not yet set — fires with `{ start, end: null }`).

---

## 4. Range selection state machine

- **State A** (no start, or both set): first click → sets `start`, clears `end`
- **State B** (start set, no end): second click → sets `end` (sorted: lo = min(start, click), hi = max). Closes the picker.

---

## 5. Display formatting

`formatRange(strValue)` produces: `"Mon DD, YYYY – Mon DD, YYYY"` (e.g. `"Jun 3, 2026 – Jun 10, 2026"`). When start only: `"Jun 3, 2026 – …"`. Empty → placeholder text.

---

## 6. Two-month layout

Left calendar shows `cursor` month. Right calendar shows the following month (wrapping Dec→Jan).

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required + error), FR-2 (open/onOpenChange + onFocus/onBlur), FR-3 (size/fillMode/rounded — resolves G-DRP4).
> Supersedes: G-DRP4 (size/fillMode/rounded declared but not applied — REQUIRED at Wave-N), G-DRP5 (format declared but unused — remains reserved; same path as DateInput spinners/steps).

### 7. Additional props

```typescript
// Added in Wave-N
interface DateRangePickerPropsExpansion {
  // FR-2: controlled popup (resolves audit show/defaultShow + onOpen/onClose + G-DRP1)
  open?: boolean
  defaultOpen?: boolean
  onOpenChange?: (open: boolean) => void

  // FR-2: focus events (resolves audit onFocus/onBlur P1)
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // Range semantics (resolves audit allowReverse + focusedDate)
  allowReverse?: boolean    // default: true — auto-correct end < start
  focusedDate?: Date        // calendar opens positioned here
  activeRangeEnd?: 'start' | 'end'   // which endpoint next click updates

  // FR-1
  required?: boolean
  error?: boolean           // drives aria-invalid on trigger inputs

  // ARIA (resolves audit ariaLabelledBy + ariaDescribedBy + G-DRP6)
  ariaLabelledBy?: string
  ariaDescribedBy?: string
  ariaLabel?: string
}
```

### 8. open / onOpenChange (FR-2 — resolves G-DRP1)

`open` / `onOpenChange` replace the ad-hoc `setOpen` calls. Outside-pointer-down fires `onOpenChange(false)`. `Escape` fires `onOpenChange(false)`. This resolves G-DRP1 (no click-outside close).

### 9. allowReverse

When `allowReverse=true` (default): if the user selects an end date before the start date, the values are silently swapped before `onValueChange` fires. State-machine step 2 in §4 is amended: sort is now `allowReverse ? sort(lo, hi) : as-clicked`. When `allowReverse=false`, swapping does not occur and end < start is allowed (useful for negative-interval display).

### 10. size / fillMode / rounded (FR-3 — resolves G-DRP4)

`size`, `fillMode`, and `rounded` are now applied to the trigger button. Vocabulary migrates to canonical `'sm'|'md'|'lg'` (FR-3) with legacy aliases at Wave-N. The Styling contract Wave-N expansion §8 documents the class recipes.

### 11. required / error (FR-1)

`required` → `aria-required` on trigger. `error=true` → `aria-invalid` on trigger and applies the error border token. Validation messages composed via FormField / ValidationMessage externally.
