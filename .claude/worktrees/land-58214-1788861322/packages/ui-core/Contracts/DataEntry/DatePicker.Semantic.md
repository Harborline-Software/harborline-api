# DatePicker — Semantic Contract

- **Component:** DatePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DatePicker.Interaction.md) · [Accessibility](./DatePicker.Accessibility.md) · [Styling](./DatePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DatePicker.tsx`
- **Catalog row:** #38 DatePicker (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — extends DateInput with hand-rolled calendar popover

---

## 1. Component purpose

**DatePicker** — a date input with an inline calendar popover. Extends DateInput (inheriting all its props) and adds a custom calendar overlay for graphical date selection. The text input and calendar are kept in sync.

---

## 2. Props

```typescript
interface DatePickerProps extends DateInputProps {
  // all DateInputProps (value, defaultValue, onChange, min, max, disabled, readonly, etc.)
  // See [DateInput.Semantic.md §2](./DateInput.Semantic.md) for the full DateInputProps definition.
  calendarView?: 'month' | 'year' | 'decade'  // only 'month' implemented
  weekNumber?: boolean                          // default: false — show week number column
  disabledDates?: Date[] | ((date: Date) => boolean)  // individual date disable
  footer?: boolean                              // default: true — show 'Today' button
}
```

---

## 3. Calendar behavior

The internal `Calendar` sub-component renders a single-month grid. Navigation (`‹`/`›`) changes the visible month cursor. Clicking a day calls `onSelect(date)` which closes the popover.

`isDisabled(date, disabledDates)`: returns true if date is in the disabled array (by `toDateString()` comparison) or if the predicate function returns true.

---

## 4. Today shortcut

When `footer=true`, a "Today" link renders below the calendar grid. Clicking it: creates `new Date()`, sets hours to 0:0:0:0 (local midnight), calls `select(d)`.

---

## 5. Controlled / uncontrolled

Inherits DateInput's controlled/uncontrolled behavior. The calendar and the DateInput stay in sync via the same `current` value.

---

## 6. calendarView limitation

`calendarView` prop is declared but only `'month'` view is implemented. `'year'` and `'decade'` views are not rendered.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required), FR-2 (open/onOpenChange + onFocus/onBlur), FR-3 (size axes via DateInput inheritance).
> Supersedes: G-DP2 (calendarView year/decade — now mandatory Wave-N P1), G-DP1 + G-DP5 (calendar keyboard + grid role — mandatory Wave-N via Calendar Wave-N expansion).

### 7. Additional props

```typescript
// Added in Wave-N
interface DatePickerPropsExpansion {
  // FR-2: controlled popup (replaces ad-hoc setOpen; resolves audit show/defaultShow + onOpen/onClose)
  open?: boolean
  defaultOpen?: boolean                           // uncontrolled initial open state
  onOpenChange?: (open: boolean) => void          // Radix-style; supersedes onOpen/onClose (Kendo names map: onOpen → onOpenChange(true), onClose → onOpenChange(false))

  // FR-2: focus events
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // focusedDate: calendar opens positioned on this date rather than selected value
  focusedDate?: Date
  onFocusedDateChange?: (date: Date) => void

  // year/decade views (resolves audit gap + G-DP2)
  // calendarView is now fully functional for 'month'|'year'|'decade'

  // FR-1
  required?: boolean   // threaded to internal DateInput via FormFieldContext

  // ARIA (resolves audit ariaDescribedBy/ariaLabelledBy)
  ariaDescribedBy?: string
  ariaLabelledBy?: string
}
```

Inherits all `DateInput` Wave-N expansion props via `DateInputProps`.

### 8. open / onOpenChange (FR-2)

`open` is the controlled popup-visibility prop. When `open` is omitted, popup state is internal (uncontrolled, seeded by `defaultOpen`). `onOpenChange(true)` fires when the popup opens (button click, keyboard `Alt+Down`); `onOpenChange(false)` fires when it closes (day selection, `Escape`, outside-click, `Tab`-blur). This replaces the implicit `setOpen` calls documented in the existing Interaction contract.

### 9. calendarView (resolves G-DP2)

`calendarView='year'` and `calendarView='decade'` are now fully functional. The internal Calendar sub-component receives `activeView` and `onActiveViewChange` from the Wave-N Calendar Semantic expansion. `focusedDate` overrides the calendar's initial cursor position when the popover opens.

### 10. required (FR-1)

`required` is passed into the composite via `FormFieldContext`. The internal `DateInput` surfaces `aria-required`. The host `FormField` shows the asterisk. No per-picker validation message — use `ValidationMessage` composed externally.

### 11. ARIA props

`ariaDescribedBy` and `ariaLabelledBy` are forwarded to the internal `DateInput` component (which has its own Wave-N ARIA expansion). The trigger button also receives `aria-describedby` when provided.
