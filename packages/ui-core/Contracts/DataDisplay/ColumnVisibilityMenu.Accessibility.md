# ColumnVisibilityMenu — Accessibility Contract

- **Component:** ColumnVisibilityMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColumnVisibilityMenu.Semantic.md) · [Interaction](./ColumnVisibilityMenu.Interaction.md) · [Accessibility](./ColumnVisibilityMenu.Accessibility.md) · [Styling](./ColumnVisibilityMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ColumnVisibilityMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ColumnVisibilityMenu is an interactive popover control with checkboxes. Its accessibility contract must cover: trigger button naming, popover ARIA structure, checkbox labelling, keyboard navigation, and focus management on open/close.

---

## 2. Trigger button

| Attribute | Value | Rationale |
|---|---|---|
| `aria-label` | `"Column visibility"` | Button's visible label is only the icon + "Columns" text (hidden on small screens). The `aria-label` provides a consistent accessible name. |
| `aria-haspopup` | `"dialog"` | Closed 2026-07-17 by PR 2742. |
| `aria-expanded` | Radix-managed on the Trigger via `asChild` | Closed 2026-07-17 by PR 2742; verified in rendered output. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Checkbox labelling

Each Radix Checkbox has `aria-label={col.label}`. This ensures screen readers announce the column name when reading the checkbox.

Pattern: `<Checkbox.Root aria-label="Invoice Number" checked={true} />`

SR reads: "Invoice Number, checkbox, checked".

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Required column indication

Required columns render their Checkbox with `disabled={true}`. SR reads: "Invoice Number, checkbox, checked, dimmed" (browser/AT specific wording for disabled).

**Gap:** No additional AT cue explains WHY the column is required. Hosts may want to add a `(required)` suffix to the column label or a `title` attribute. Currently not implemented.

---

## 5. Hidden count badge

The trigger shows a count of hidden columns (e.g., "2"). Its accessible name includes that count, for example `"Column visibility, 2 hidden"`.

Closed 2026-07-17 by PR 2742: the dynamic trigger `aria-label` exposes the hidden count to AT users.

---

## 6. Keyboard navigation

Fully handled by Radix Popover + Radix Checkbox. See Interaction contract §5 for key map.

| Concern | Handling |
|---|---|
| Focus enters popover | Radix moves focus to first interactive element (first checkbox) on open |
| Focus returns on close | Radix returns focus to the trigger button on Escape / click-outside |
| Tab cycle within popover | Radix traps focus within popover until dismissed |
| Checkbox toggle via Space | Radix Checkbox handles natively |

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; SC 2.4.3 Focus Order.

---

## 7. Touch target

The trigger button has `px-3 py-2` padding (~32px height). Each checkbox row has `py-1.5` with a 14px checkbox target. The Radix Checkbox target may be below the 24×24 minimum per WCAG 2.2 SC 2.5.8 on some viewports.

**Known gap (A3):** Checkbox click target is small (~14px). PAO Styling should increase the hit area to 24×24 via `p-[5px]` or similar.

---

## 8. Known gaps

| # | Item | Resolution path |
|---|---|---|
| A1 | `aria-haspopup` missing on trigger; Radix may inject it via `asChild` | Closed 2026-07-17 by PR 2742: explicit `aria-haspopup="dialog"` shipped. |
| A2 | Hidden-count not reflected in trigger's accessible name | Closed 2026-07-17 by PR 2742: dynamic `aria-label` now includes the count. |
| A3 | Checkbox touch/click target may be below 24×24px | PAO Styling increases target area |
| A4 | No AT cue for why a required column cannot be toggled | Add `(required)` suffix or `title` attribute on the row |
