# DateRangePicker — Interaction Contract

- **Component:** DateRangePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateRangePicker.Semantic.md) · [Accessibility](./DateRangePicker.Accessibility.md) · [Styling](./DateRangePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateRangePicker.tsx`
- **Catalog row:** #39 DateRangePicker (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Trigger button click** → `setOpen(!open)` (toggle)
- **Second day click** (completes range) → `setOpen(false)`
- No click-outside close mechanism is implemented.

---

## 2. Day click

`handleDay(ymd)`:
1. If no start set, or both already set: `next = { start: ymd, end: null }` → fires `onValueChange({ start, end: null })`
2. If start set but no end: sorts lo/hi → `next = { start: lo, end: hi }` → fires `onValueChange({ start, end })` → `setOpen(false)`

---

## 3. Hover preview

`onMouseEnter` on each day button → `setHover(ymd)`. Hover clears on `onMouseLeave` on the popover container. In state B (start set, no end), hovered days show the prospective range highlight.

---

## 4. Month navigation

Left calendar: `‹` button decrements cursor month (handles Jan→Dec wrap).

Right calendar: `›` button increments cursor month (handles Dec→Jan wrap).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DRP1 | High | No click-outside to close — picker stays open until a range is completed or trigger is clicked again | Accepted-risk M1 |
| G-DRP2 | High | No keyboard navigation within calendar grids | Accepted-risk M1 |
| G-DRP3 | Medium | `disabledDates` prop declared but not passed to `MonthGrid` — individual date disabling is not implemented | Accepted-risk M1 |
| G-DRP4 | Medium | `size`, `fillMode`, `rounded` props declared but not applied to the trigger button styling | Accepted-risk M1 |
| G-DRP5 | Low | `format` prop declared but not used | Accepted-risk M1 |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (open/onOpenChange + onFocus/onBlur).
> Supersedes: G-DRP1 (no click-outside close — RESOLVED), G-DRP2 (no keyboard nav — REQUIRED at Wave-N via Calendar integration).

### 6. open / onOpenChange (FR-2 — resolves G-DRP1)

| Trigger | onOpenChange |
|---|---|
| Trigger button click | `onOpenChange(!current)` |
| Range completion (second day click) | `onOpenChange(false)` |
| `Escape` while popup open | `onOpenChange(false)` |
| Outside pointer-down | `onOpenChange(false)` |
| `Alt+Down` on focused trigger | `onOpenChange(true)` |

Outside-pointer-down is detected via a `useEffect` + `document.pointerdown` outside-ref listener (same pattern recommended in DatePicker Interaction §1 warning).

### 7. Calendar keyboard navigation (resolves G-DRP2)

The internal dual-month calendar component implements the Calendar Wave-N Interaction expansion (arrow-key grid nav, roving tabIndex, Enter/Space selection). Cross-month boundary navigation wraps per MultiViewCalendar Wave-N Interaction §3. G-DRP2 is REQUIRED at Wave-N — no longer accepted-risk.

### 8. activeRangeEnd keyboard

`Tab` from the start trigger moves `activeRangeEnd` to `'end'`. When `activeRangeEnd='end'`, arrow/Enter updates the end endpoint. `Escape` resets to `activeRangeEnd='start'` and clears the partial range.

### 9. onFocus / onBlur

Surface at the outer DateRangePicker container. Do NOT close the popup on blur — close exclusively via `onOpenChange` triggers in §6.

### 10. allowReverse

When `allowReverse=true`: `handleDay` step 2 sorts lo/hi before calling `onValueChange`. The existing sort in §2 already does this; the semantic addition is that when `allowReverse=false` the sort is skipped (end may be < start).
