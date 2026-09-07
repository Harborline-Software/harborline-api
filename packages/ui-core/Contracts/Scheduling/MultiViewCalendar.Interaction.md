# MultiViewCalendar — Interaction Contract

- **Component:** MultiViewCalendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./MultiViewCalendar.Semantic.md) · [Accessibility](./MultiViewCalendar.Accessibility.md) · [Styling](./MultiViewCalendar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #113 MultiViewCalendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik MultiViewCalendar baseline)

---

## 1. Interaction

Extends Calendar — see `Calendar.Interaction.md`.

Multi-month: keyboard `ArrowLeft/Right/Up/Down` navigate within the focused month and wrap to the adjacent visible month when crossing a month boundary.

---

## 2. Known gaps

None beyond Calendar gaps.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (focus + popup events). Inherits Calendar Wave-N Interaction expansion (year/decade view keyboard nav, activeView Escape).
> Supersedes: nothing — additive.

### 3. Cross-month boundary arrow navigation

Arrow key navigation that crosses a month boundary wraps to the adjacent visible pane. Left-pane rightmost cell → Arrow Right → first cell of right pane. Right-pane leftmost cell → Arrow Left → last cell of left pane. `PageUp/PageDown` move all panes by one month simultaneously.

### 4. activeRangeEnd keyboard

When `activeRangeEnd='start'`: arrow/Enter updates the range start; `Tab` moves focus to the end input (if host provides one) and advances `activeRangeEnd` to `'end'`. When `activeRangeEnd='end'`: arrow/Enter updates the range end. `Escape` resets `activeRangeEnd` to `'start'` and clears the partial selection.

### 5. allowReverse behavior

When the user completes a range where `end < start` (either via click or keyboard), and `allowReverse=true`, the values are silently swapped before `onRangeChange` fires. Focus remains on the cell that was last activated.

### 6. Year / decade view

Inherits Calendar Wave-N Interaction §7 (year/decade keyboard nav). In multi-view mode, all panes change view level simultaneously. Each pane shows a different year (or decade) offset matching the pane's current month offset.
