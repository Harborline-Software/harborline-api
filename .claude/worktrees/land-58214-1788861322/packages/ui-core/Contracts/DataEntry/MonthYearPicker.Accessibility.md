# MonthYearPicker — Accessibility Contract

- **Component:** MonthYearPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MonthYearPicker.Semantic.md) · [Interaction](./MonthYearPicker.Interaction.md) · [Accessibility](./MonthYearPicker.Accessibility.md) · [Styling](./MonthYearPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MonthYearPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

MonthYearPicker uses a `role="group"` container with a grid of native `<button>`
elements for month selection. This contract names the ARIA surface, live regions,
and known gaps.

---

## 2. ARIA structural roles

| Element | Role / attribute | Notes |
|---|---|---|
| Outer container `<div>` | `role="group"` | Groups the year nav + month grid |
| Outer container | `aria-label={label ?? 'Month and year picker'}` | Group label |
| Outer container | `aria-required={required}` | Only when `required` is truthy |
| Outer container | `aria-disabled={disabled}` | Announces group as disabled |
| Year display `<span>` | `aria-live="polite"` | Announces year changes to AT |
| Month grid `<div>` | `role="grid"` + `aria-label={"Months for " + displayYear}` | Grid role on the month grid |
| Month `<button>` elements | `role="gridcell"` + `aria-selected={isSelected}` | Each month button in the grid |
| Month button | `aria-label={"Jan 2026"}` etc. | Full month + year label |
| Prev year button | `aria-label="Previous year"` | Named by explicit label |
| Next year button | `aria-label="Next year"` | Named by explicit label |
| Required asterisk `<span>` | `aria-hidden="true"` | Visual only |
| Error `<p>` | `role="alert"` | Error message is a live region |

---

## 3. Group labelling

The outer container uses `role="group"` with `aria-label`. When `label` is
provided, the group aria-label equals the label text. When `label` is absent,
it falls back to `'Month and year picker'`.

> **Gap A-1:** The outer group does not use `aria-labelledby` pointing to the
> visible label element's id. It uses `aria-label` with the same string instead.
> These are equivalent for AT purposes, but `aria-labelledby` is preferred when
> a visible label element exists (WCAG SC 4.1.2).

---

## 4. Year live region

The year display `<span>` has `aria-live="polite"`. When the user navigates
years, AT announces the new year value.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 5. Month grid and selection

The month grid uses `role="grid"` and each month button uses `role="gridcell"`
with `aria-selected`. This correctly communicates selected state for grid
widgets.

> **Gap A-2:** No arrow-key navigation is implemented despite `role="grid"`.
> The ARIA grid pattern requires arrow keys to navigate between gridcells.
> Current implementation relies on Tab order. Fix: implement roving tabIndex
> + arrow key handlers.

---

## 6. `aria-required`

`aria-required={required}` is set on the group container when `required` is
truthy. This communicates the required state at the group level.

---

## 7. `aria-disabled`

`aria-disabled={disabled}` is set on the group container. The individual month
buttons also receive native `disabled` when `disabled === true`.

> **Note:** `aria-disabled` on a container does not automatically propagate to
> children — the children also receive native `disabled`, which is correct here.

---

## 8. Error state

When `error` is provided, `<p role="alert">` renders with the error string.
AT announces the error as a live region on render.

> **Gap A-3:** No `aria-invalid` on the group or any child element. AT users
> hear the error message on render but do not receive `aria-invalid` feedback
> when focused on a month cell.

---

## 9. Focus ring

Month buttons use default browser focus (no custom ring suppression). The
implementation applies `transition-colors` but does not define `focus:ring`
classes on month buttons.

> **Gap A-4 (important):** Month buttons have no explicit `focus-visible:ring-*`
> recipe. Browsers provide a default focus indicator, which may fail WCAG
> 2.4.13 Focus Appearance requirements or be visually inconsistent with the
> fleet design system. Add explicit `focus-visible:ring-2 focus-visible:ring-blue-500`.

**WCAG citations:** WCAG 2.2 SC 2.4.7, SC 2.4.13.

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Group uses `aria-label` not `aria-labelledby` to visible label | WCAG SC 4.1.2 | Use `aria-labelledby` pointing to label element's id |
| A-2 | No arrow-key grid navigation despite `role="grid"` | WCAG SC 2.1.1, WAI-ARIA grid | Implement roving tabIndex + arrow handlers |
| A-3 | No `aria-invalid` on selection state | WCAG SC 3.3.1, SC 4.1.2 | Add `aria-invalid` to group when `error` is set |
| A-4 | Month buttons lack explicit focus ring | WCAG SC 2.4.7, SC 2.4.13 | Add `focus-visible:ring-2 focus-visible:ring-blue-500` |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required/aria-invalid via FormField), FR-2 (onFocus/onBlur). DataGrid #35 Accessibility §2 grid pattern applies to month grid.
> Supersedes: A-2 (no arrow-key nav — REQUIRED at Wave-N, RESOLVED via Interaction Wave-N §9), A-3 (no aria-invalid — RESOLVED), A-4 (no focus ring — RESOLVED).

### 11. Wave-N ARIA changes

**A-2 RESOLVED:** Arrow-key grid navigation implemented per Interaction Wave-N §9. The `role="grid"` pattern (already declared in §5) is now fully wired: roving tabIndex + arrow-key handlers.

**A-3 RESOLVED:** `aria-invalid="true"` is set on the group container when `error` is truthy OR when `FormFieldContext` signals an invalid state. This satisfies WCAG SC 3.3.1 and SC 4.1.2.

**A-4 RESOLVED:** Month buttons now carry `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1` (using the design-system `ring` token rather than hardcoded `ring-blue-500`). This satisfies WCAG SC 2.4.7 and SC 2.4.13.

### 12. A-1 partial resolution (Wave-N)

A-1 is partially resolved: the outer group now uses `aria-labelledby` pointing to the visible label's `id` when both `label` and `id` props are provided. Falls back to `aria-label` when only `label` is present (same as before). This improves WCAG SC 4.1.2 conformance.

### 13. aria-describedby (resolves S-2)

`aria-describedby` now wired on the group container: combines `FormFieldContext.describedBy` and the `ariaDescribedBy` prop, space-separated. Links hint text, error text, or any external descriptor to the group. WCAG SC 1.3.1 satisfied.

### 14. Disabled month cells

Disabled months (from `disabledMonths` / `minMonth` / `maxMonth`) carry `aria-disabled="true"` on their `<button>` element. They also receive the native `disabled` attribute to prevent focus and click. AT announces them as "dimmed" or "unavailable" depending on browser.

### 15. Focus events (FR-2)

`onFocus` / `onBlur` wired at the outer group container. Enables FormField focus-ring wiring: when focus enters any child, FormField can apply the focus ring to the group border.
