# CalendarDayPicker — Semantic Contract

- **Component:** CalendarDayPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CalendarDayPicker.Interaction.md) · [Accessibility](./CalendarDayPicker.Accessibility.md) · [Styling](./CalendarDayPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CalendarDayPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled calendar day-grid

---

## 1. Purpose

CalendarDayPicker is an inline calendar widget for selecting a single calendar
day. It renders a full month grid with previous/next month navigation and
supports marking individual dates with dot or highlight variants. It is
intentionally inline (always visible), not a popover — hosts that want a
date-picker popover pair it with a trigger button themselves.

---

## 2. Data model

CalendarDayPicker is semi-controlled. The `value` prop is the currently
selected date; `onChange` fires when the user clicks a day. The displayed
month (cursor) is internal state initialized from `value` (or today when
`value` is absent) and navigated by the user.

```typescript
interface DayMark {
  date: string          // ISO 8601 date string — "YYYY-MM-DD"
  variant?: 'dot' | 'highlight'
  color?: string        // CSS color string; used when variant === 'dot'
  label?: string        // accessible label for the dot mark
}

interface CalendarDayPickerProps {
  value?: string | null      // selected date in "YYYY-MM-DD" format; null = no selection
  onChange?: (date: string) => void  // fired with "YYYY-MM-DD" on day click
  minDate?: string           // "YYYY-MM-DD"; days before this are disabled
  maxDate?: string           // "YYYY-MM-DD"; days after this are disabled
  marks?: DayMark[]          // per-day decoration markers
  disabled?: boolean         // disables the entire picker
  className?: string         // additional class(es) on the root element
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string \| null` | `undefined` | The selected date as an ISO 8601 date string ("YYYY-MM-DD"). When `null` or undefined, no day is selected. The calendar cursor initializes to the month containing `value`, or today's month when absent. |
| `onChange` | `(date: string) => void` | `undefined` | Fires with the selected date string when the user clicks an enabled day. Optional — the picker may be used read-only without a handler. |
| `minDate` | `string` | `undefined` | Lower bound (inclusive) in "YYYY-MM-DD". Days before this string comparison are rendered disabled and non-interactive. |
| `maxDate` | `string` | `undefined` | Upper bound (inclusive) in "YYYY-MM-DD". Days after this are rendered disabled. |
| `marks` | `DayMark[]` | `[]` | Per-day decorations. Each mark is matched by its `date` string. Two variants: `dot` renders a coloured circle below the day number; `highlight` paints the day button with an amber background. Marks on disabled days render visually; the disabling takes precedence for interaction. |
| `disabled` | `boolean` | `false` | When `true`, the whole picker is non-interactive (pointer-events-none, 50% opacity). Individual days are effectively unclickable. |
| `className` | `string` | `''` | Merged onto the root `<div>`. |

### 3.1 Date string format

All date I/O uses ISO 8601 short format: `"YYYY-MM-DD"`. Lexicographic string
comparison is used for min/max/disabled checks — do not pass locale-formatted
dates.

### 3.2 Cursor / navigation state

The displayed month is internal state. It initializes from `value` (or today
when `value` is absent) and is mutated by the prev/next month buttons. Changing
`value` via the prop after mount does NOT update the cursor month — the cursor
is not synced to prop changes (this is a gap; see §7).

### 3.3 Marks

Marks are decorative metadata keyed to specific dates. Only one mark per date
is supported (the last `DayMark` with a given `date` in the array wins, as the
implementation uses `Map` insertion order). `dot` and `highlight` are mutually
exclusive per date in the current implementation.

### 3.4 FormField integration

CalendarDayPicker does **not** consume `FormFieldContext` and does not read
`describedBy`. It is typically used as a standalone or in a custom wrapping
layout rather than inside a FormField.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(date: string)` | The user clicks an enabled (not `disabled`, not range-excluded) day button. Payload is the ISO date string of the clicked day. Not fired on navigation (prev/next month). |

---

## 5. Slots

No slot extensibility. Header content (month/year display) and weekday headers
are fixed. Day cell rendering is fixed (number + optional dot/highlight).

---

## 6. Component composition

CalendarDayPicker renders as a self-contained inline block. It does NOT wrap
a FormField or integrate with FormFieldContext. Hosts that need a label/error
treatment must supply those externally.

---

## 7. Deferred / known gaps

| Gap | Description |
|---|---|
| Cursor does not sync to `value` changes | Once mounted, changing `value` to a date in a different month does not update the displayed month. The selected day is highlighted if it happens to be in the currently displayed month. |
| No `error` prop | CalendarDayPicker does not expose an `error` boolean or string for validation feedback. |
| No `aria-describedby` / FormField integration | No `useFormField()` integration. |
| No year navigation | Only month navigation is provided; reaching a year far from the current requires many clicks. |
| Single mark per date | The `marks` array supports one mark per date string (last write wins). |
| No keyboard day navigation | Arrow keys do not move focus between day cells; only Tab navigates between the focusable buttons. |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required, error), FR-2 (onFocus/onBlur), FR-3 (size axis).
> Supersedes: "No keyboard day navigation" gap — REQUIRED at Wave-N (Level-A). "No year navigation" gap — REQUIRED at Wave-N. "Cursor does not sync" — RESOLVED. "No error prop" — RESOLVED.

### 8. Additional props

```typescript
// Added in Wave-N
interface CalendarDayPickerPropsExpansion {
  // FR-2: focus events
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // FR-1
  required?: boolean
  error?: boolean           // exposes an error state (resolves "No error prop" gap)

  // Year navigation (resolves "No year navigation" gap)
  // Achieved via the activeView prop integrating the Calendar year/decade view pattern
  showYearNav?: boolean     // default: false — shows a year-jump view (clicking month/year header)

  // FR-3: size axis
  size?: 'sm' | 'md' | 'lg'     // default: 'md'

  // ARIA
  ariaDescribedBy?: string
  ariaLabelledBy?: string
  ariaLabel?: string
  id?: string
}
```

### 9. Year navigation (resolves "No year navigation")

When `showYearNav=true`: the month/year header becomes a button. Clicking it switches to a year-view grid (12 months × 1 year, same layout as MonthYearPicker's grid). Clicking a month in year view returns to the month-day view for that month. This follows the Calendar Interaction Wave-N §7 pattern.

When `showYearNav=false` (default): the header remains a static label, month-only navigation unchanged.

### 10. error prop

`error=true` adds `aria-invalid="true"` to the root group and applies the error border token. No inline error message — use FormField / ValidationMessage for the message.

### 11. Cursor sync (resolves "Cursor does not sync")

`displayMonth` (internal cursor) is synced to `value` via `useEffect([value])`. When the `value` prop changes to a date in a different month after mount, the cursor updates to show that month.

### 12. required (FR-1)

`required` → `aria-required="true"` on the root group. When used inside FormField, the asterisk is rendered by FormField. When used standalone, `required` has no visual effect beyond the ARIA attribute — hosts must render an asterisk externally.
