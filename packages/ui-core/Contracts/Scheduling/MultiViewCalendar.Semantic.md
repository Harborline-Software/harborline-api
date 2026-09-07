# MultiViewCalendar — Semantic Contract

- **Component:** MultiViewCalendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./MultiViewCalendar.Interaction.md) · [Accessibility](./MultiViewCalendar.Accessibility.md) · [Styling](./MultiViewCalendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik MultiViewCalendar baseline)
- **Catalog row:** #113 MultiViewCalendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik MultiViewCalendar baseline)

---

## 1. Component purpose

**MultiViewCalendar** — displays multiple adjacent calendar months side-by-side. Used for date-range selection where the start and end dates may span month boundaries (e.g. hotel booking, leave request). Thin wrapper around Calendar with `views` prop.

---

## 2. Props (planned)

```typescript
interface MultiViewCalendarProps extends CalendarProps {
  views?: number           // number of months to show side-by-side; default: 2
}
```

Inherits all props from `CalendarProps`. See `Calendar.Semantic.md` for the full interface.

---

## 3. Month layout

`views=2` renders two Calendar month grids side-by-side, sharing a single navigation header. Previous/Next arrows advance all visible months simultaneously by 1.

---

## 4. Range selection

Same semantics as Calendar with `range=true`. In-range highlighting spans across the month boundary between visible months.

---

## 5. Relationship to Calendar

MultiViewCalendar is Calendar with `views > 1`. Exists as a named export for discoverability in range-picker contexts.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (validation), FR-2 (focus + popup events), FR-3 (appearance axes).
> Supersedes: nothing — additive.

### 6. Additional props

```typescript
// Added in Wave-N
interface MultiViewCalendarPropsExpansion {
  // FR-2: popup control (resolves audit gap: onOpen/onClose, allowReverse, activeRangeEnd)
  allowReverse?: boolean       // default: true — auto-correct end < start by swapping; audit gap P1
  activeRangeEnd?: 'start' | 'end'
  // FR-2: focus events
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>
  // Controlled view
  activeView?: 'month' | 'year' | 'decade'
  defaultActiveView?: 'month' | 'year' | 'decade'
  onActiveViewChange?: (view: 'month' | 'year' | 'decade') => void
  // FR-1
  required?: boolean
  // ARIA
  ariaDescribedBy?: string
  ariaLabelledBy?: string
  id?: string
  tabIndex?: number
}
```

`MultiViewCalendarProps` extends `CalendarProps` (which has its own Wave-N expansion) plus `MultiViewCalendarPropsExpansion`.

### 7. allowReverse

When `allowReverse=true` (default) and the user completes a range where `end < start`, the component swaps the two dates silently before firing `onRangeChange`. The Calendar Interaction Wave-N expansion covers the keyboard mechanics.

### 8. activeRangeEnd

`activeRangeEnd` controls which endpoint the next click or arrow-key selection updates. Defaults to `'start'` when no selection exists; automatically advances to `'end'` after a start is set. Hosts that render start/end text inputs can drive this explicitly to allow the user to modify either endpoint independently.

### 9. activeView propagation

All visible month panes share a single `activeView` state. View transitions advance/retreat all panes simultaneously. `onActiveViewChange` fires once per transition regardless of `views` count.
