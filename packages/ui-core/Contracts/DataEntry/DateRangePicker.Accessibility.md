# DateRangePicker — Accessibility Contract

- **Component:** DateRangePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateRangePicker.Semantic.md) · [Interaction](./DateRangePicker.Interaction.md) · [Styling](./DateRangePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateRangePicker.tsx`
- **Catalog row:** #39 DateRangePicker (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="button"` | Trigger `<button>` | Prevents form submission |
| `disabled` | Trigger `<button>` | Blocked state |
| `aria-hidden="true"` | 📅 emoji `<span>` | Decorative |
| `type="button"` | Day `<button>` | Prevents form submission |
| `disabled` | Out-of-range day `<button>` | Blocked state |
| `focus-visible:ring-2 focus-visible:ring-blue-500` | Day `<button>` | Visible focus ring |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DRP2 | High | No keyboard navigation within calendar grids — keyboard-only users cannot select dates | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
| G-DRP6 | High | Trigger button has no `aria-haspopup`, `aria-expanded`, or `aria-label` — AT does not know a calendar opens | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-DRP7 | High | Calendar grids have no `role="grid"` or `aria-label` with month/year — AT reads them as unnamed button groups | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-DRP8 | Medium | Day buttons have no `aria-label` with full date — AT reads only the day number | Accepted-risk M1 |
| G-DRP9 | Medium | No `aria-live` for month navigation announcements | Accepted-risk M1 |
| G-DRP10 | Low | Hover range preview has no AT equivalent — range preview not announced | Accepted-risk M1 |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required, aria-invalid), FR-2 (aria-expanded, aria-haspopup). DataGrid #35 Accessibility §2 is the fleet canonical grid pattern — applies to internal calendar grids.
> Supersedes: G-DRP2 (keyboard nav — REQUIRED, RESOLVED), G-DRP6 (trigger aria-haspopup/expanded — REQUIRED, RESOLVED), G-DRP7 (grid role — REQUIRED, RESOLVED).

### 3. Trigger button ARIA (Wave-N — resolves G-DRP6)

| Attribute | Element | Value |
|---|---|---|
| `aria-haspopup="dialog"` | Trigger `<button>` | Signals popup type |
| `aria-expanded` | Trigger `<button>` | `"true"` / `"false"` per `open` state |
| `aria-label` | Trigger `<button>` | `ariaLabel` prop or default `"Select date range"` |
| `aria-labelledby` | Trigger `<button>` | `ariaLabelledBy` prop when provided |
| `aria-describedby` | Trigger `<button>` | `ariaDescribedBy` prop when provided |
| `aria-controls={popupId}` | Trigger `<button>` | References popup container |
| `aria-required` | Trigger `<button>` | `"true"` when `required` prop is set (FR-1) |
| `aria-invalid` | Trigger `<button>` | `"true"` when `error=true` (FR-1) |

### 4. Calendar popup ARIA (Wave-N — resolves G-DRP7)

| Attribute | Element | Value |
|---|---|---|
| `role="dialog"` | Popup container | Dialog widget |
| `aria-label="Select date range"` | Popup container | Names the dialog |
| `aria-modal="true"` | Popup container | AT virtual cursor trap |

Each calendar month grid uses `role="grid"` + `aria-label="{Month} {Year}"`. Implements full roving tabIndex + gridcell pattern per Calendar Wave-N Accessibility §7. G-DRP7 RESOLVED.

### 5. aria-live range announcement (Wave-N)

An `aria-live="polite"` region announces: "Start date set to {date}" when start is chosen; "Range set: {start} to {end}" when range is complete; "Range cleared" when reset.

### 6. Focus management

On popup open: focus moves to the currently selected start-date cell (or today). On popup close: focus returns to the trigger button.
