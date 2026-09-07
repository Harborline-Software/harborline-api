# Calendar — Interaction Contract

- **Component:** Calendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Calendar.Semantic.md) · [Accessibility](./Calendar.Accessibility.md) · [Styling](./Calendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #112 Calendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Calendar baseline)

---

## 1. Date selection

Click a day cell: fires `onChange(date)`. Clicking a selected date: fires `onChange(null)` (deselects).

---

## 2. Range selection

When `range=true`: first click sets start; pointer hover highlights interim days; second click sets end and fires `onRangeChange([start, end])`. Click on a day after range is complete restarts range selection from the new date.

---

## 3. Month navigation

Previous/Next arrows: advance month by 1. Header click: switch to year view. Year view month click: return to month view for that month.

---

## 4. Keyboard navigation

- `ArrowLeft/Right`: move focus one day.
- `ArrowUp/Down`: move focus one week.
- `PageUp/PageDown`: advance/retreat one month.
- `Home/End`: move to first/last day of the focused week.
- `Enter/Space`: select the focused date.

---

## 5. Disabled dates

Disabled date cells are not focusable and not clickable. Arrow key navigation skips them.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CAL1 | Low | Year view keyboard navigation not specified | Accepted-risk M1; standard arrow-key month navigation applies |

---

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (focus + popup events).
> Supersedes: G-CAL1 (year/decade view keyboard nav is now specified here).

### 7. Year view keyboard navigation

In year view (12-month grid): `ArrowLeft/Right` move focus one month; `ArrowUp/Down` move focus one row (3 months); `Home/End` move to first/last month of the year; `PageUp/PageDown` step year backward/forward. `Enter/Space` select the focused month (transitions to month view for that month). `Escape` returns to month view without changing the active month selection.

In decade view (10-year grid): same arrow/page pattern applies scaled to years. `Enter/Space` selects the focused year (transitions to year view).

This resolves G-CAL1 (Accepted-risk M1 gap) — year/decade keyboard nav is now mandatory at Wave-N.

### 8. activeView keyboard affordance

When `activeView` is controlled, pressing `Escape` in year or decade view fires `onActiveViewChange('month')`. The header button always accepts `Enter/Space` to trigger the view advance.

---

## Polish expansion (2026-06-11 — Polish-pilot, see _shared/design/polish-gate.md)

Reference bar: modern picker month grids. REQUIRED additions:

1. **Range mode.** `mode?: 'single' | 'range'` (default `'single'`).
   In range mode `value` is `{ start: string | null; end: string | null }`;
   first activation sets start, second sets end (swapping if inverted);
   onChange delivers the range object. In-range days get a connecting wash;
   endpoints get the selected fill.
2. **Week numbers.** `showWeekNumbers?: boolean` renders an ISO week column.
3. **Bounds styling.** Days outside `min`/`max` are visibly disabled
   (reduced contrast, no hover, aria-disabled) — not merely inert.
4. **State styling pass.** Today = ring; selected = solid fill; hover = wash;
   keyboard focus = visible ring distinct from hover. Header: chevron icon
   buttons + month/year quick-nav button (existing year view) with tightened
   density per the Styling contract.
