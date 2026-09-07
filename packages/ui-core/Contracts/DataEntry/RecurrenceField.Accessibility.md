# RecurrenceField — Accessibility Contract

- **Component:** RecurrenceField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RecurrenceField.Semantic.md) · [Interaction](./RecurrenceField.Interaction.md) · [Accessibility](./RecurrenceField.Accessibility.md) · [Styling](./RecurrenceField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RecurrenceField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

RecurrenceField is a composite control with a frequency selector, interval
input, weekday toggles, and end-date picker. This contract names the ARIA
surface for each sub-control and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Root `<div>` | (none) | `space-y-3` container |
| Label `<label>` (when `label` supplied) | (linked) | Visible; `htmlFor="{id}-freq"` |
| Frequency `<select>` | implicit `listbox` | `id="{id}-freq"` |
| Interval `<input type="number">` | implicit `spinbutton` | No explicit label — see gap A-1 |
| Interval unit span | (decorative) | Inline text `"day(s)"` etc. |
| Weekday group `<div>` | `role="group"` | `aria-label="Repeat on days"` |
| Weekday `<button>` elements | `button` | `aria-pressed={selected}` |
| End date `<label>` | (linked) | `htmlFor="{id}-end"`; text `"End date"` |
| End date `<input type="date">` | implicit `textbox` / date picker | `id="{id}-end"` |

---

## 3. Label linkage

When `label` is provided, `<label htmlFor="{id}-freq">` links to the frequency
selector. AT announces the label when the frequency selector is focused.

> **Gap A-1 (important):** The interval `<input type="number">` has no label.
> It is visually accompanied by inline text ("Every", "day(s)") but has no
> `aria-label` or `<label>`. AT users focusing the interval input hear only
> "spin button" with no context. Fix: add `aria-label="Repeat every N"` or use
> a `<label>` element.

---

## 4. Frequency selector

A native `<select>` with five `<option>` elements. AT users can navigate
options with arrow keys and hear each option's text. Standard `<select>`
semantics.

---

## 5. Weekday group

`role="group"` with `aria-label="Repeat on days"` correctly groups the seven
toggle buttons. Each button has `aria-pressed` to communicate selection state.

**WCAG citations:** WCAG 2.2 SC 4.1.2 (pressed state), SC 1.3.1 (group).

---

## 6. End date

The end date label (`"End date"`) links to the input via `htmlFor="{id}-end"`.
Standard `<input type="date">` browser semantics.

---

## 7. Conditional visibility

Sub-controls are conditionally rendered (React removes them from the DOM).
When frequency changes from `'weekly'` to another value, the weekday group is
removed. AT receives appropriate focus and announcement as focus moves to the
next remaining element.

> **Gap A-2:** When frequency changes, no live region announces the change to
> AT users who didn't activate the select. Consider `aria-live="polite"` on a
> region describing the current recurrence summary.

---

## 8. Disabled state

All sub-controls receive native `disabled`. AT announces each as unavailable.

---

## 9. Focus rings

| Control | Recipe |
|---|---|
| Frequency `<select>` | `focus:outline-none focus:ring-1 focus:ring-blue-500 focus:border-blue-500` |
| Interval `<input>` | `focus:outline-none focus:ring-1 focus:ring-blue-500 focus:border-blue-500` |
| Weekday buttons | `focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500` |
| End date `<input>` | Same as select/interval recipe |

Weekday buttons use `ring-2`; others use `ring-1`.

> **Gap A-3:** Inconsistent focus ring widths (`ring-1` vs `ring-2`). WCAG
> 2.4.13 Focus Appearance (Minimum) requires a 2px indicator for full
> compliance. The `ring-1` (1px) on select/interval/end-date falls short.
> Standardize to `ring-2`.

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Interval input has no label | WCAG SC 1.3.1, SC 4.1.2 | Add `aria-label` or `<label>` |
| A-2 | No live region for conditional control visibility changes | WCAG SC 4.1.3 | Add `aria-live` region for recurrence summary |
| A-3 | Inconsistent `ring-1` vs `ring-2` on sub-controls | WCAG SC 2.4.13 | Standardize to `ring-2` |
