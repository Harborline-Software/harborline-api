# Calendar — Accessibility Contract

- **Component:** Calendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Calendar.Semantic.md) · [Interaction](./Calendar.Interaction.md) · [Styling](./Calendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #112 Calendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Calendar baseline)

---

## 1. Widget role

`role="group"` on the calendar root with `aria-label="Calendar"`. Follows ARIA APG Date Picker (Grid Pattern).

---

## 2. Month grid

`role="grid"` on the day table. Each row: `role="row"`. Each day cell: `role="gridcell"` with `aria-label="{day of week}, {month} {day}, {year}"`.

Selected day: `aria-selected="true"`. Today: `aria-current="date"`. Disabled days: `aria-disabled="true"`.

---

## 3. Navigation buttons

Previous/Next month: `<button aria-label="Previous month">` / `<button aria-label="Next month">`. Month/year header: `<button aria-label="Change month/year view">`.

---

## 4. Range selection announcement

When range mode: `aria-live="polite"` region announces start date set, end date set, and total days selected.

---

## 5. Focus management

On mount, focus is placed on `value` date if set, otherwise on today's date. After month navigation, focus moves to the same day-of-month in the new month (clamped to last day if needed).

---

## 6. Known gaps

None identified for forward-spec.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (focus events, onFocus/onBlur passthrough). DataGrid #35 Accessibility §2 is the fleet canonical `role="grid"` + roving-tabIndex + arrow-key pattern; Calendar MUST conform.
> Supersedes: nothing — additive.

### 7. ARIA grid pattern (Level-A — MANDATORY)

The day grid in month view MUST implement the full ARIA grid pattern:

| Element | Role / attribute | Value |
|---|---|---|
| Day table | `role="grid"` | — |
| Each week row | `role="row"` | — |
| Each day cell | `role="gridcell"` | — |
| Day cell | `aria-label` | Human-readable: `"{weekday}, {Month} {day}, {year}"` via `Intl.DateTimeFormat` |
| Day cell | `aria-selected` | `"true"` when selected; omit when not |
| Day cell | `aria-disabled` | `"true"` when disabled (out of range or in disabledDates) |
| Today cell | `aria-current` | `"date"` |

**Roving tabIndex:** exactly one day cell carries `tabIndex=0` at any time (the focused day). All others carry `tabIndex=-1`. Arrow keys move the roving focus; `Enter/Space` selects.

**Week-number column (when showWeekNumbers=true):** each week number cell is `role="rowheader"` with `aria-label="Week {n}"`.

**WCAG citations:** SC 2.1.1 (Level A), SC 4.1.2 (Level A). Both are BLOCKING before v1 ship.

### 8. Year / decade view ARIA

Year view grid: `role="grid"` + `aria-label="{year} — select a month"`. Each month cell: `role="gridcell"` + `aria-label="{Month} {year}"` + `aria-selected`. Roving tabIndex applies.

Decade view grid: `role="grid"` + `aria-label="Select a year"`. Each year cell: `role="gridcell"` + `aria-label="{year}"` + `aria-selected`. Roving tabIndex applies.

### 9. ARIA linking props

`ariaDescribedBy` → `aria-describedby` on root. `ariaLabelledBy` → `aria-labelledby` on root (replaces hardcoded `aria-label="Calendar"`). Both are passed through from the Semantic expansion §10.

### 10. Focus event passthrough (FR-2)

`onFocus` / `onBlur` handlers wired to the root container allow popup hosts to detect composite blur without polling. The Calendar itself does not manage popup state — it merely surfaces the events.
