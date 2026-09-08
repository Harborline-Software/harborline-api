# MultiViewCalendar — Accessibility Contract

- **Component:** MultiViewCalendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./MultiViewCalendar.Semantic.md) · [Interaction](./MultiViewCalendar.Interaction.md) · [Styling](./MultiViewCalendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #113 MultiViewCalendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik MultiViewCalendar baseline)

---

## 1. Accessibility

Extends Calendar — see `Calendar.Accessibility.md`.

Each visible month grid has its own `role="grid"` with `aria-label="{Month} {Year}"` so screen readers can distinguish between months.

---

## 2. Known gaps

None beyond Calendar gaps.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2. Calendar Wave-N Accessibility expansion §7–§10 applies to each pane. DataGrid #35 Accessibility §2 is the fleet canonical grid pattern.
> Supersedes: nothing — additive.

### 3. Per-pane ARIA grid (Level-A — MANDATORY)

Each visible month pane is an independent `role="grid"` with `aria-label="{Month} {Year}"` (e.g., `"June 2026"`, `"July 2026"`). Screen readers can announce "June 2026 grid, 5 rows, 7 columns" separately for each pane, allowing users to orient themselves in the multi-month layout.

All Calendar Wave-N Accessibility §7 requirements (roving tabIndex, gridcell, aria-selected, aria-disabled, aria-current, full-date aria-label via Intl.DateTimeFormat) apply to each pane individually.

### 4. Cross-pane roving tabIndex

There is exactly one `tabIndex=0` cell across the ENTIRE multi-pane composite (not one per pane). Arrow key navigation moves the roving focus across pane boundaries (§3 in Wave-N Interaction). Each pane's remaining cells carry `tabIndex=-1`.

### 5. ARIA linking / focus props

`ariaDescribedBy`, `ariaLabelledBy`, `id` propagate to the outer composite wrapper. `onFocus` / `onBlur` surface at the outer wrapper level for popup hosts.
