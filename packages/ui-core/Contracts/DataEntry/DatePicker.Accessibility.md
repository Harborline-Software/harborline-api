# DatePicker — Accessibility Contract

- **Component:** DatePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DatePicker.Semantic.md) · [Interaction](./DatePicker.Interaction.md) · [Styling](./DatePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DatePicker.tsx`
- **Catalog row:** #38 DatePicker (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-label="Open calendar"` | Calendar toggle `<button>` | Accessible label |
| `type="button"` | Calendar toggle `<button>` | Prevents form submission |
| `type="button"` | Prev/next month `<button>` | Prevents form submission |
| `type="button"` | Day `<button>` | Prevents form submission |
| `disabled` | Disabled day `<button>` | Blocked state |

DateInput ARIA is inherited from the DateInput component.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DP1 | High | No keyboard navigation within the calendar grid (arrow keys) | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
| G-DP5 | High | Calendar grid has no `role="grid"` or `role="application"` — AT reads it as a series of buttons | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-DP6 | Medium | Day buttons have no `aria-label` with full date — AT reads only the day number `"5"` | Accepted-risk M1 |
| G-DP7 | Medium | No `aria-live` region — month navigation does not announce the new month to AT | Accepted-risk M1 |
| G-DP8 | Low | 📅 emoji on calendar toggle button is not `aria-hidden` — some AT may announce it | Fix-in-M1: replace emoji with SVG icon + `aria-hidden="true"` on the icon; emoji-as-decoration is not reliable across AT |

> **Co-dependency note:** G-DP1 and G-DP5 must ship together. Implementing arrow-key calendar navigation (G-DP1) without the ARIA grid role (G-DP5) is non-conformant — AT interprets arrow keys in a plain `<div>` of buttons unpredictably. Fix both before shipping keyboard-accessible date selection.

> **Inherited gap:** DatePicker embeds DateInput. DateInput's G-DI2 ("no locale-aware date format validation error feedback") applies to the DatePicker's text entry path. Keyboard-only users who type an invalid date in the DateInput field may not receive AT feedback. See the RA-1 compensating requirement in `DatePicker.Interaction.md`.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required), FR-2 (open/onOpenChange → aria-expanded/aria-haspopup). DataGrid #35 Accessibility §2 is the fleet canonical grid pattern — applies to internal calendar.
> Supersedes: G-DP1 and G-DP5 are RESOLVED at Wave-N (no longer accepted-risk; BLOCKING). G-DP8 (emoji on toggle) is also resolved.

### 3. Trigger button ARIA (Wave-N — resolves G-DP1, G-DP5, G-DP8)

| Attribute | Element | Value |
|---|---|---|
| `aria-haspopup="dialog"` | Toggle `<button>` | Signals popup type to AT |
| `aria-expanded` | Toggle `<button>` | `"true"` / `"false"` per `open` state (FR-2) |
| `aria-label="Open calendar"` | Toggle `<button>` | Accessible label (icon-only button) |
| `aria-controls={popupId}` | Toggle `<button>` | References the calendar popup `id` |
| SVG icon + `aria-hidden="true"` | Icon element | Replaces 📅 emoji; resolves G-DP8 |

### 4. Calendar popup ARIA (Wave-N)

| Attribute | Element | Value |
|---|---|---|
| `role="dialog"` | Popup container | Marks as dialog widget for AT |
| `aria-label="Calendar"` | Popup container | Names the dialog |
| `aria-modal="true"` | Popup container | Traps AT virtual cursor inside popup |

The internal Calendar component implements the full `role="grid"` pattern (Calendar Wave-N Accessibility §7). G-DP1 (no keyboard nav) and G-DP5 (no grid role) are RESOLVED.

### 5. Focus management

On popup open: focus moves to the selected date cell (or today if no selection). On popup close: focus returns to the toggle button. `Escape` closes and returns focus.

### 6. aria-invalid / required (FR-1)

`required` → `aria-required="true"` on the internal DateInput. `aria-invalid="true"` on the DateInput when `FormFieldContext` signals an error. The trigger button does not carry `aria-invalid`.

### 7. ARIA linking

`ariaDescribedBy` forwarded to internal DateInput `aria-describedby`. `ariaLabelledBy` forwarded to internal DateInput `aria-labelledby`.
