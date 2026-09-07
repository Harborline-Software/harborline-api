# CalendarDayPicker — Interaction Contract

- **Component:** CalendarDayPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CalendarDayPicker.Semantic.md) · [Interaction](./CalendarDayPicker.Interaction.md) · [Accessibility](./CalendarDayPicker.Accessibility.md) · [Styling](./CalendarDayPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CalendarDayPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how CalendarDayPicker responds to user interaction —
day selection, month navigation, disabled day handling, and the global disabled
state.

---

## 2. Day selection

- **Trigger:** click on an enabled day button.
- **Condition:** the day must not be outside the `[minDate, maxDate]` range and
  the component must not be globally `disabled`.
- **Callback:** `onChange(ymd)` fires with the ISO date string ("YYYY-MM-DD").
- **No deselection:** clicking the already-selected day fires `onChange` again
  with the same date. There is no built-in deselect (clear) gesture.
- **Side effect:** the day button receives a selected visual treatment
  (blue filled circle). The cursor month does not change.

---

## 3. Month navigation

| Control | Behaviour |
|---|---|
| "Previous month" button (‹) | Decrements the cursor month; wraps from January to December of the prior year. |
| "Next month" button (›) | Increments the cursor month; wraps from December to January of the next year. |

Navigation is always enabled regardless of `minDate` / `maxDate` — the user
can navigate to months where all days are out of range. Those days are rendered
as disabled.

Navigation does NOT trigger `onChange`.

---

## 4. Disabled days (range-excluded)

- Days where `ymd < minDate` or `ymd > maxDate` are rendered with
  `text-gray-200 cursor-not-allowed` and the native `disabled` attribute.
- Click has no effect; `onChange` does not fire.
- Tab focus is skipped (native disabled).

---

## 5. Global disabled state

- **Trigger:** `disabled === true` on the root component.
- **Behaviour:** `pointer-events-none opacity-50` on the root wrapper.
  Individual day buttons and nav buttons are rendered but unclickable.
- `onChange` cannot fire when globally disabled.

---

## 6. Keyboard behaviour

| Key | Behaviour |
|---|---|
| Tab / Shift+Tab | Moves focus between the "Previous month" button, day buttons (enabled only), and "Next month" button in document order. |
| Enter / Space | Activates the focused button (select day or navigate). Native button behaviour. |
| Arrow keys | No custom grid-navigation. Arrow keys do not move focus between day cells. |
| Escape | No effect. |

> **Known gap:** There is no arrow-key grid navigation for the day grid. A
> compliant calendar widget (ARIA `grid` pattern) would support arrow keys to
> move between cells. This is a gap vs. ARIA Authoring Practices Grid pattern.

---

## 7. Today highlight

The current date (determined at render from `new Date()`) receives a border
ring (`border-2 border-blue-400`) and blue text styling. `aria-current="date"`
is set on that button. This is cosmetic and does not affect selection behaviour.

---

## 8. Marks interaction

- `'highlight'` variant: the day button receives a warm amber background tint.
  The day is still selectable unless disabled.
- `'dot'` variant: a small circle appears below the day number. If `mark.label`
  is provided, it is set as `aria-label` on the dot span. The day is still
  selectable unless disabled.
- Marks on disabled (range-excluded) days are rendered but the day is
  non-interactive.

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | No arrow-key grid navigation | Implement ARIA grid row/cell pattern with arrow key handlers |
| I-2 | Clicking selected day re-fires onChange with same value | Add guard: if `ymd === value`, skip `onChange` or add a `clearable` prop |
| I-3 | Month cursor does not track `value` prop changes | Sync cursor to `value` in a `useEffect` or derive cursor from `value` |
| I-4 | No "go to today" shortcut | Add a "Today" button or keyboard shortcut |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (onFocus/onBlur). DataGrid #35 Accessibility §2 is the fleet canonical grid pattern.
> Supersedes: I-1 (no arrow-key grid nav — REQUIRED at Wave-N, Level-A), I-3 (cursor sync — RESOLVED via Semantic Wave-N §11).

### 10. Arrow-key grid navigation (resolves I-1 — Level-A MANDATORY)

CalendarDayPicker's day grid MUST implement the roving tabIndex ARIA grid pattern consistent with DataGrid #35 Accessibility §2:

- `ArrowRight`: move roving focus one day forward. Wraps to first day of next row (not next month).
- `ArrowLeft`: move roving focus one day back. Wraps to last day of previous row (not previous month).
- `ArrowDown`: move roving focus one week (7 days) forward. If the next-week cell is in the next month, navigate to that month and position focus on the corresponding day.
- `ArrowUp`: move roving focus one week (7 days) back. If the previous-week cell is in the prior month, navigate to that month.
- `Home`: move focus to the first enabled day of the current week.
- `End`: move focus to the last enabled day of the current week.
- `PageUp`: navigate to the same day of the prior month.
- `PageDown`: navigate to the same day of the next month.
- `Enter / Space`: select the focused day (fires `onChange` if day is enabled).

Roving tabIndex: exactly one day button carries `tabIndex=0`; all others carry `tabIndex=-1`. Disabled days (range-excluded) are skipped — focus advances to the next enabled day.

This resolves I-1. It is a blocking requirement before v1 ship (WCAG SC 2.1.1 + 4.1.2 Level A).

### 11. Year navigation interaction (showYearNav=true)

Header button click → switch to year view (12-month grid). In year view:
- `ArrowLeft/Right`: move focus one month.
- `ArrowUp/Down`: move focus one row (3 months per row in the 4×3 grid).
- `Enter/Space`: select focused month → return to day view for that month.
- `Escape`: return to day view without changing the displayed month.

### 12. onFocus / onBlur (FR-2)

`onFocus` / `onBlur` wired at the root container. Enable FormField focus-ring wiring and composite host blur detection.
