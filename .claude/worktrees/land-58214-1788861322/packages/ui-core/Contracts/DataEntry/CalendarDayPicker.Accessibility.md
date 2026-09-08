# CalendarDayPicker — Accessibility Contract

- **Component:** CalendarDayPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CalendarDayPicker.Semantic.md) · [Interaction](./CalendarDayPicker.Interaction.md) · [Accessibility](./CalendarDayPicker.Accessibility.md) · [Styling](./CalendarDayPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CalendarDayPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CalendarDayPicker is a custom calendar widget composed of native `<button>`
elements inside a `<div>` grid. It uses several ARIA attributes to expose its
semantics to assistive technology. This contract names the ARIA surface,
keyboard coverage, and known accessibility gaps.

---

## 2. ARIA structural roles

| Element | Role / attribute | Notes |
|---|---|---|
| Root `<div>` | (no role) | Contains the nav header and day grid. |
| Day `<button>` elements | implicit `button` | Each day is a native `<button>`. |
| Today's day button | `aria-current="date"` | Marks the current calendar date. |
| Selected day button | `aria-pressed={isSelected}` | Communicates toggle/selection state. |
| Day button (disabled range) | native `disabled` | Excluded days use the native `disabled` attribute. |
| Dot mark `<span>` | `aria-label={mark.label}` (when provided) | Decorative span; label is optional. |
| Navigation buttons | `aria-label="Previous month"` / `aria-label="Next month"` | Named by explicit `aria-label`. |

---

## 3. Button labels

Every day button has `aria-label={ymd}` (the ISO date string, e.g.
`"2026-06-15"`). This gives AT a machine-readable but not human-friendly date
announcement.

> **Gap A-1:** ISO date strings ("2026-06-15") are not a natural-language label
> for AT users. A human-readable label such as "June 15, 2026" (using
> `Intl.DateTimeFormat`) would improve the experience. The current
> implementation uses the raw ISO string.

---

## 4. Selection state

Selected days use `aria-pressed={isSelected}` (boolean). This communicates
"this day is currently selected" to AT via the pressed/toggle-button pattern.

> **Note:** The WAI-ARIA Authoring Practices date-picker pattern recommends
> `aria-selected` on gridcells rather than `aria-pressed` on buttons, but
> the current implementation does not use the grid pattern. The `aria-pressed`
> approach is workable with the existing flat-button layout.

---

## 5. Today's date

`aria-current="date"` is set on the button representing today. AT will announce
the button as the current date in the calendar context.

---

## 6. Keyboard navigation

| Key | Behaviour |
|---|---|
| Tab | Moves focus to the next interactive element (nav buttons, enabled day buttons) in document order. |
| Shift+Tab | Moves focus backwards. |
| Enter / Space | Activates the focused button. |
| Arrow keys | No grid navigation implemented. |

> **Gap A-2 (critical):** The WAI-ARIA Authoring Practices grid-based calendar
> pattern requires arrow keys to navigate between day cells. The current
> implementation has no `onKeyDown` handlers on day buttons for arrow navigation.
> Tab order through all enabled day cells is technically functional but creates
> an excessive tab-stop count for keyboard users. Fix path: implement grid
> pattern with `role="grid"` / `role="gridcell"`, roving tabIndex, and
> arrow-key handlers.

---

## 7. Focus ring

Navigation buttons use `focus-visible:ring-2 focus-visible:ring-blue-500`.
Day buttons use `focus-visible:ring-2 focus-visible:ring-blue-500`.

**WCAG 2.4.7 (Focus Visible):** satisfied — visible ring on focus.
**WCAG 2.4.13 (Focus Appearance):** `ring-2` (2px) used here; this satisfies
the minimum area requirement for the nav buttons. Day cells (h-8 w-8 = 32×32px)
with `ring-2` provide a perimeter of ~128px × 2 = 256 minimum indicator area —
compliant.

---

## 8. Global disabled state

When `disabled === true`, the root wrapper receives `pointer-events-none
opacity-50`. Individual buttons are NOT given the native `disabled` attribute
— only pointer events are suppressed via CSS. AT may still announce these
buttons as interactive.

> **Gap A-3:** When the component is globally disabled, native `disabled` is
> not set on nav buttons or day buttons (only `pointer-events-none` is applied).
> Screen readers may announce buttons as interactive when they are not. Fix:
> pass `disabled` prop to all child buttons when the root `disabled` prop is
> true, OR add `aria-disabled="true"` to the root container with a descriptive
> label.

---

## 9. Dot mark labels

Dot marks render a `<span>` with `aria-label={mark.label}` when `mark.label`
is provided. When `mark.label` is omitted, the span has no label and is
effectively invisible to AT.

**Recommendation:** Hosts should always supply `label` on DayMark entries when
marks carry meaningful information (e.g., "Event scheduled", "Payment due").

---

## 10. Color as the only channel

The `mark.color` prop allows arbitrary CSS colors for dot marks. There is no
enforcement that these colors meet the 3:1 non-text contrast ratio against the
host background.

> **Gap A-4:** Dot mark colors are host-supplied and may fail WCAG SC 1.4.11
> (Non-text Contrast). The `mark.label` mechanism provides a non-color channel,
> but only if the host supplies it.

---

## 11. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Day buttons labeled with ISO string not human date | WCAG SC 1.3.1, 2.5.3 | Use `Intl.DateTimeFormat` for accessible label |
| A-2 | No arrow-key grid navigation | WCAG SC 2.1.1, WAI-ARIA grid pattern | Implement ARIA grid pattern with roving tabIndex |
| A-3 | Global disabled via CSS only, not native `disabled` | WCAG SC 4.1.2 | Apply `disabled` to child buttons when root disabled |
| A-4 | Dot mark colors have no contrast enforcement | WCAG SC 1.4.11 | Document color contract; add host guidance |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required, aria-invalid — error prop), FR-2 (onFocus/onBlur). DataGrid #35 Accessibility §2 is the fleet canonical `role="grid"` + roving-tabIndex pattern.
> Supersedes: A-1 (ISO string labels — RESOLVED), A-2 (no arrow-key nav — REQUIRED at Wave-N, RESOLVED), A-3 (global disabled via CSS only — RESOLVED).

### 12. ARIA grid pattern (Wave-N — resolves A-2 — Level-A MANDATORY)

The day grid MUST adopt the full `role="grid"` pattern:

| Element | Role / attribute | Value |
|---|---|---|
| Day grid `<div>` | `role="grid"` | — |
| Week `<div>` | `role="row"` | — |
| Day cell `<button>` | `role="gridcell"` | — |
| Day cell | `aria-label` | `"{weekday}, {Month} {day}, {year}"` via `Intl.DateTimeFormat` (resolves A-1) |
| Day cell | `aria-selected` | `"true"` when selected; absent when not |
| Day cell | `aria-disabled` | `"true"` when range-excluded |
| Today cell | `aria-current` | `"date"` |
| Roving cell | `tabIndex` | `0` (all others `−1`) |

Week-number column (if future `showWeekNumbers` prop): `role="rowheader"` + `aria-label="Week {n}"`.

**A-2 RESOLVED.** This change is BLOCKING before v1 ship (WCAG SC 2.1.1 + 4.1.2 Level A).

**A-1 RESOLVED.** `aria-label` now uses `Intl.DateTimeFormat('en', { weekday:'long', year:'numeric', month:'long', day:'numeric' }).format(date)`.

### 13. Global disabled ARIA (resolves A-3)

When `disabled === true`: individual day buttons and nav buttons receive native `disabled` attribute (not only `pointer-events-none`). Root container also receives `aria-disabled="true"`. This resolves A-3.

### 14. aria-invalid / error prop (FR-1)

When `error=true`: `aria-invalid="true"` on the root container. Also add `aria-describedby={ariaDescribedBy}` when provided. WCAG SC 3.3.1 and 4.1.2.

### 15. aria-required (FR-1)

`required=true` → `aria-required="true"` on root container (role="group" or role="grid" outer wrapper).

### 16. ARIA linking

`ariaDescribedBy` → `aria-describedby` on root. `ariaLabelledBy` → `aria-labelledby` on root. `ariaLabel` → `aria-label` on root (fallback). `id` on root for external label association.
