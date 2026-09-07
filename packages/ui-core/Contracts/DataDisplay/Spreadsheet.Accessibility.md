# Spreadsheet — Accessibility Contract

- **Component:** Spreadsheet
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Spreadsheet.Semantic.md) · [Interaction](./Spreadsheet.Interaction.md) · [Accessibility](./Spreadsheet.Accessibility.md) · [Styling](./Spreadsheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Spreadsheet.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| `<table>` | (implicit) | `table` (HTML default) |
| `<th>` column headers | (implicit) | `columnheader` |
| `<th>` row headers | (implicit) | `rowheader` |
| `<td>` cells | `tabIndex` | `0` when selected, `-1` when not |
| Edit `<input>` | (no explicit role) | text input inside a cell |

No explicit `role`, `aria-label`, or `aria-describedby` attributes are set on the grid, cells, or formula bar.

---

## 2. Keyboard accessibility (current)

The `tabIndex` roving pattern allows the selected cell to be reachable via Tab. Key handlers on the `<td>` respond to `Enter`, `F2`, `Delete`, and `Backspace`.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPRA1 | High | No `role="grid"` or `role="gridcell"` — component renders `<table>` without grid ARIA semantics; WCAG SC 4.1.2 (Level A) violation | Fix-deferred M2 — requires architectural rework to adopt WAI-ARIA `role="grid"` container + `role="gridcell"` cells |
| G-SPRA2 | High | No `aria-label` on the table — unnamed table; screen readers announce it without context; WCAG SC 1.3.1 (Level A) | Fix-deferred M2 — add `aria-label` prop (or `aria-labelledby` pointing at a visible caption) to the Spreadsheet component |
| G-SPRA3 | High | No cell value announcements — selected cell has `tabIndex=0` but no `aria-label` or `aria-describedby`; WCAG SC 4.1.2 (Level A) | Fix-deferred M2 — announce cell address + value on selection via `aria-label` on the focused cell element |
| G-SPRA4 | Medium | Formula bar not accessible — non-interactive `<div>` not linked to selected cell via `aria-describedby` or `aria-controls` | Fix-deferred M2 — add `role="status"` + `aria-live="polite"` to formula bar; link via `aria-controls` on the selected cell |
| G-SPRA5 | Medium | No screen-reader edit mode announcement — entering edit mode moves focus to inline `<input>` but does not announce "editing" | Fix-deferred M2 — add `aria-label="Editing cell {addr}"` on the inline input, or aria-live announcement on mode transition |
| G-SPRA6 | Medium | No `aria-readonly` on locked cells — locked cells have visual style but no `aria-readonly="true"` or `aria-disabled` | Fix-deferred M2 — add `aria-readonly="true"` (for read-only but focusable) or `aria-disabled="true"` per locked-cell semantics |
| G-SPRA7 | Low | Column/row `<th>` headers missing `scope="col"` / `scope="row"` — some screen readers use scope to associate headers with data cells | Fix-deferred M2 — add `scope` attribute to all `<th>` elements |
