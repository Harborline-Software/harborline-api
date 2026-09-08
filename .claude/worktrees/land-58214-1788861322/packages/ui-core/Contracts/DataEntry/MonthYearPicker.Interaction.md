# MonthYearPicker — Interaction Contract

- **Component:** MonthYearPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MonthYearPicker.Semantic.md) · [Interaction](./MonthYearPicker.Interaction.md) · [Accessibility](./MonthYearPicker.Accessibility.md) · [Styling](./MonthYearPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MonthYearPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how MonthYearPicker responds to user interaction —
month selection, year navigation, keyboard handling, and disabled state.

---

## 2. Month selection

- **Trigger:** click on a month button.
- **Condition:** component must not be globally `disabled`.
- **Callback:** `onChange(toValue(displayYear, month))` fires with `"YYYY-MM"`.
- **Visual feedback:** the selected month button receives `bg-blue-600 text-white`.
- **No deselection:** clicking the selected month fires `onChange` again.

---

## 3. Year navigation

| Control | Behaviour |
|---|---|
| Prev year button | Decrements `displayYear` if `displayYear > effectiveMin`. |
| Next year button | Increments `displayYear` if `displayYear < effectiveMax`. |

Both buttons are `disabled` when at the boundary.

Navigation does NOT fire `onChange`. The year displayed changes; the selected
value is unchanged.

---

## 4. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** All month buttons and year navigation buttons receive native
  `disabled`. `onChange` cannot fire. Visual: `opacity-50` on the container.
- The `selectMonth` function also guards: `if (disabled) return` before calling
  `onChange`.

---

## 5. Keyboard behaviour

| Key | Element | Behaviour |
|---|---|---|
| Tab / Shift+Tab | All buttons | Move focus through the year nav buttons and month buttons in document order. |
| Enter / Space | Month button | Select the month (`selectMonth` fires). Explicit `onKeyDown` handles Enter and Space with `e.preventDefault()`. |
| Enter / Space | Year nav button | Native button activation (prev/next year). |
| Arrow keys | Month button | No grid navigation; default behaviour only (browser scroll if not prevented). |

> **Known gap I-1:** No arrow-key grid navigation across month cells.

---

## 6. Current month highlight

The current calendar month (determined from `new Date()`) receives
`bg-blue-50 text-blue-700 ring-1 ring-blue-300` when it falls in the
currently displayed year and is not selected.

---

## 7. Required indicator

When `required === true`, a red asterisk `*` is appended to the label text
with `aria-hidden="true"` (visual only — the asterisk is not announced). The
group also carries `aria-required={required}`.

---

## 8. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | No arrow-key navigation across month grid | Implement roving tabIndex + arrow key handlers |
| I-2 | No individual-month disabling | Add `disabledMonths` or `minMonth`/`maxMonth` props |
| I-3 | Clicking selected month re-fires onChange | Add guard; or add `clearable` prop |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (onFocus/onBlur). DataGrid #35 Accessibility §2 is the fleet canonical grid pattern — MonthYearPicker's month grid MUST conform.
> Supersedes: I-1 (no arrow-key grid nav — REQUIRED at Wave-N), I-2 (no per-month disabling — REQUIRED at Wave-N via Semantic Wave-N §8).

### 9. Arrow-key grid navigation (resolves I-1 — Level-A MANDATORY)

MonthYearPicker's month grid uses a roving tabIndex grid navigation pattern consistent with DataGrid #35 Accessibility §2:

- `ArrowRight`: move focus one month forward (Jan→Feb, ..., Nov→Dec; Dec wraps to Jan of same row or no-wrap depending on layout — 4×3 grid: row wraps at end of row, does NOT advance to the next displayed year).
- `ArrowLeft`: move focus one month back.
- `ArrowDown`: move focus one row down (3 months forward; wraps from last row to first row).
- `ArrowUp`: move focus one row up (3 months back; wraps from first row to last row).
- `Home`: move focus to January.
- `End`: move focus to December.
- `Enter / Space`: select the focused month (fires `onChange`), same guard as click (disabled months skipped).
- `Tab`: exits the grid into the year navigation buttons or next focusable element in document order.

Roving tabIndex: exactly one month button carries `tabIndex=0`; others carry `tabIndex=-1`. Initial roving focus is on the selected month (or the first enabled month when no selection).

Disabled months (from `disabledMonths` / `minMonth` / `maxMonth`) are skipped by arrow keys — focus advances past them.

### 10. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` fire at the outer group container. They are used by FormField focus-ring wiring and by any composing host that needs to detect composite focus transitions.

### 11. disabledMonths behavior

Clicking or activating (Enter/Space) a disabled month does nothing. Arrow keys skip disabled months. The `selectMonth` guard is amended: `if (isDisabled(month)) return;`.
