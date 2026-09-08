# Calendar — Semantic Contract

- **Component:** Calendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Calendar.Interaction.md) · [Accessibility](./Calendar.Accessibility.md) · [Styling](./Calendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Calendar / React-DayPicker baseline)
- **Catalog row:** #112 Calendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Calendar baseline)

---

## 1. Component purpose

**Calendar** — a single-month calendar grid for selecting a single date or a range of dates. Inline (not inside a picker popover) — always visible in the layout. Used for booking surfaces, task due-date pickers, and dashboard date filters.

---

## 2. Props (planned)

```typescript
interface CalendarProps {
  value?: Date | null                         // controlled single-date selection
  defaultValue?: Date                         // uncontrolled default
  onChange?: (date: Date | null) => void
  range?: boolean                             // enable range selection; default: false
  rangeValue?: [Date | null, Date | null]     // controlled range [start, end]
  defaultRangeValue?: [Date | null, Date | null]
  onRangeChange?: (range: [Date | null, Date | null]) => void
  month?: Date                                // controlled month view
  defaultMonth?: Date
  onMonthChange?: (month: Date) => void
  minDate?: Date
  maxDate?: Date
  disabledDates?: Date[] | ((date: Date) => boolean)
  weekStartsOn?: 0 | 1 | 2 | 3 | 4 | 5 | 6  // 0=Sunday, 1=Monday; default: 0
  showWeekNumbers?: boolean                  // default: false
  dayRender?: (date: Date) => React.ReactNode // custom day cell content
  className?: string
}
```

---

## 3. Views

**Month view** (default): 7-column grid, 5-6 rows. Navigation arrows advance/retreat by one month. Month/year header is clickable to switch to year view.

**Year view**: 12-month selector. Clicking a month returns to month view for that month.

---

## 4. Range selection

When `range=true`: first click sets start date; second click sets end date. Hovering between first and second click highlights the in-range days.

---

## 5. Disabled dates

Dates matching `disabledDates` are rendered non-interactive. If `disabledDates` is a function, it is called for each visible date cell.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (validation), FR-2 (focus + popup events), FR-3 (appearance axes).
> Supersedes: nothing in existing contract — additive only.

### 6. Additional props

```typescript
// Added in Wave-N
interface CalendarPropsExpansion {
  // FR-2: focus event passthrough
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // FR-1: validation surface
  required?: boolean      // drives aria-required on root group; see FR-1

  // Controlled view (resolves audit gap: activeView + onActiveViewChange)
  activeView?: 'month' | 'year' | 'decade'
  defaultActiveView?: 'month' | 'year' | 'decade'   // default: 'month'
  onActiveViewChange?: (view: 'month' | 'year' | 'decade') => void

  // ARIA linking (resolves audit gaps: ariaDescribedBy, ariaLabelledBy)
  ariaDescribedBy?: string
  ariaLabelledBy?: string

  // FR-2: id for label association
  id?: string
  tabIndex?: number
}
```

`CalendarProps` is extended with `CalendarPropsExpansion`. Existing props are unchanged.

### 7. activeView / view navigation

When `activeView` is provided the component is controlled on view level. When omitted, view state is internal (`defaultActiveView`). Clicking the month/year header advances the view toward 'decade'; clicking a decade cell → year cell → month view narrows it back. `onActiveViewChange` fires on every transition.

### 8. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` are forwarded to the root focus-management container. They receive the native `FocusEvent`. Useful for calendar-in-popover hosts that need to detect when focus leaves the entire widget.

### 9. required (FR-1)

`required` sets `aria-required="true"` on the `role="group"` root. It does not affect selection semantics — Calendar does not validate itself; hosts use FormField / ValidationMessage.

### 10. ARIA linking

`ariaDescribedBy` → `aria-describedby` on root. `ariaLabelledBy` → `aria-labelledby` on root (supersedes the default `aria-label="Calendar"`). `id` on root for external label association.
