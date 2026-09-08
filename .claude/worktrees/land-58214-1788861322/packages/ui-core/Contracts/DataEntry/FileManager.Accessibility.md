# FileManager — Accessibility Contract

- **Component:** FileManager
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileManager.Semantic.md) · [Interaction](./FileManager.Interaction.md) · [Accessibility](./FileManager.Accessibility.md) · [Styling](./FileManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileManager.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FileManager is a complex composite widget (file browser, toolbar, breadcrumb,
table or grid). This contract names the ARIA roles present, the significant
gaps, and the recommended fix paths. M1 is not WCAG 2.2 AA-compliant for
keyboard navigation — see gap G1.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Toolbar buttons (Home, Folder, Upload, Delete, View) | `button` (implicit) | Native `<button type="button">` |
| List view `<table>` | `table` (implicit) | `<thead>`, `<tbody>`, `<th>`, `<td>` |
| Grid view items `<div>` | generic (no role) | Known gap — should be `role="gridcell"` |
| `FolderIcon` / `DocIcon` SVGs | decorative | `aria-hidden` |
| Upload hidden `<input type="file">` | — | `aria-hidden`, `tabIndex={-1}` |

---

## 3. List view table structure

The list view uses a native `<table>` with `<thead>` + `<tbody>`. Column
headers are `<th scope="col">` (implicit from placement but not explicit).
Row cells are `<td>`. AT reads this as a standard table with column names
"Name", "Size", "Modified".

**Gap:** column headers lack `scope="col"` explicitly — some AT may not
associate headers with cells correctly.

---

## 4. Toolbar buttons

All toolbar buttons are native `<button type="button">` elements. Their
visible text is their accessible name. No `aria-label` additions are needed
for the text-bearing buttons. The view-toggle button label changes to show
the target mode ("Grid" / "List"), not the current mode — this can be
confusing. An `aria-label` indicating current state would improve AT clarity.

---

## 5. Rename input

The rename input (`autoFocus`) replaces the name text inline. It has no
`aria-label`; its purpose is implicit from context. AT users may not receive
context that they are in a rename interaction.

---

## 6. Selection state

Selected rows receive `bg-primary/10` visually. There is no `aria-selected`
attribute on table rows (or any ARIA selection pattern). AT cannot determine
which entries are selected.

---

## 7. Keyboard navigation

The current implementation has NO custom keyboard handling for the file list.
Table rows and grid cells are click-interactive via `onClick` but have no
`tabIndex`, no focus management, and no keyboard event handlers. Keyboard-only
users cannot navigate the file list at all.

**Recommended pattern (for M2):**
- Implement `role="grid"` on the file list with arrow-key navigation per
  WAI-ARIA Grid pattern.
- Add `tabIndex={0}` to the "active" row/cell with roving focus management.
- Map Enter to open, Space to select, Delete to delete selected.

---

## 8. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | No keyboard navigation in file list — rows/cells have no tabIndex or key handlers | Critical | Implement WAI-ARIA Grid pattern with roving tabIndex |
| G2 | No `aria-selected` on selected rows | High | Add `aria-selected={selected.has(entry.id)}` on row elements |
| G3 | Grid view cells have no ARIA role | High | Add `role="gridcell"` or migrate to `role="listbox"` + `role="option"` |
| G4 | `window.prompt` for new folder name is inaccessible in many AT/browser configs | High | Replace with inline input or modal dialog |
| G5 | Rename input has no `aria-label` | Medium | Add `aria-label="Rename {entry.name}"` |
| G6 | Table `<th>` cells lack explicit `scope="col"` | Low | Add `scope="col"` to all header cells |
