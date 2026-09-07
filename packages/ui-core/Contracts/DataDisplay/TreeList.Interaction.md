# TreeList — Interaction Contract

- **Component:** TreeList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeList.Semantic.md) · [Accessibility](./TreeList.Accessibility.md) · [Styling](./TreeList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/TreeList.tsx`
- **Catalog row:** #141 TreeList (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Expand / collapse

Clicking the expand button in the first column toggles that row's expanded state. When the row has no children, the button click is a no-op (`hasCh && toggleExpand(id)`).

---

## 2. Uncontrolled mode

`toggleExpand` updates `internalExpanded` (a `Set<string|number>`) — adds/deletes the id. Fires `onExpandChange(id, willExpand)`.

---

## 3. Controlled mode

When `expandedIds` is provided, `toggleExpand` only fires `onExpandChange` — does NOT update local state. Parent must update `expandedIds` prop.

---

## 4. Cell interaction

Cell content is caller-controlled via `column.cell`. TreeList does not handle cell clicks, row selection, or sorting. All interaction beyond expand/collapse is delegated to cell renderers.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TL1 | Medium | No keyboard navigation between rows or expand toggle via keyboard — arrow keys not handled | Accepted-risk M1 |
| G-TL2 | Low | Rows are keyed by array index — reorder-sensitive reconciliation | Accepted-risk M1 |
| G-TL3 | Low | `expandField` prop declared but has no effect — causes API confusion | Accepted-risk M1 |

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeList
audit rows 1-8. Prop/data-model shapes are owned by Semantic §FS-*; this contract owns
user-visible state transitions, edge cases, and keyboard behaviour. Cross-references to
DataGrid.Interaction.md §FS-* are cited where behaviour is identical.

---

### §FS-1 Wave — sorting + filtering + selection behaviour (audit rows 2, 3, 4)

#### §FS-1.1 Sorting behaviour

Reference: Semantic §FS-1.1; mirrors DataGrid.Interaction.md §2.

1. **Column header click.** Clicking a sortable header cycles sort direction:
   `unsorted → asc → desc → unsorted`. `onSortChange([{ field, dir }])` fires.
2. **Replacing sort.** Clicking a different header clears the prior sort and applies the new
   one. Only one entry in `sort[]` at a time (wave-3 single-sort baseline).
3. **Visual indicator.** The active sort column shows `↑` (asc) or `↓` (desc); sortable-but-
   inactive columns show a neutral sort affordance (e.g. `⇅`).
4. **No-sort click.** When `sort` is `[{ field: col.field, dir: 'desc' }]` and the user
   clicks that column again, the sort is cleared: `onSortChange([])` fires.
5. **Keyboard.** Enter or Space on a focused header button activates the sort cycle (same as
   click). This closes part of G-TL1 for the header row.

**wave-3**

---

#### §FS-1.2 Filtering behaviour

Reference: Semantic §FS-1.2; mirrors DataGrid.Interaction.md §FS-1.2.

1. **Filter input.** When `filterable === true`, each column's header row includes a text
   input. Typing fires `onColumnFiltersChange` with the updated filter state.
2. **Operator button (wave-3 extended model).** When `columnFilters` is supplied (controlled),
   each filter input shows a funnel icon that opens an operator-selection menu. Selecting an
   operator fires `onColumnFiltersChange`; behaviour is identical to DataGrid §FS-1.2 §1-7.
3. **`isnull` operator.** Selecting `isnull` hides the value input. Behaviour mirrors
   DataGrid §FS-1.2 §3.
4. **Tree semantics.** In client-side mode, a row is VISIBLE if it matches OR if any of its
   DESCENDANTS match (parent-visible-on-child-match). The expand state is not changed by
   filtering; a parent that is collapsed but has matching children appears expanded in the
   filtered view.
5. **Escape.** Pressing Escape while a filter input is focused clears that column's filter
   value and fires `onColumnFiltersChange` with the entry removed. Mirrors DataGrid §FS-1.2 §7.
6. **Debounce.** The component does NOT debounce — it fires `onColumnFiltersChange` on every
   input `change` event. Server-side callers are responsible for debouncing.

**wave-3**

---

#### §FS-1.3 Row selection behaviour

Reference: Semantic §FS-1.3; mirrors DataGrid.Interaction.md §FS-1.3.

**`'single'` mode:**

1. Clicking a data row (outside an interactive cell control) sets `rowSelection` to
   `{ [rowId]: true }` and clears all others. `onRowSelectionChange` fires.
2. Clicking the already-selected row deselects it; `onRowSelectionChange({})` fires.
3. Shift+click has no multi-select effect in single mode.

**`'multiple'` mode:**

4. The leading checkbox column behaves identically to DataGrid's `__select__` column — per-row
   checkboxes + header tri-state checkbox. See DataGrid.Interaction.md §4 for the full
   multiple-mode spec; behaviour is identical for TreeList.
5. Expand/collapse does NOT change selection state. Collapsing a parent with selected children
   retains those selections in `rowSelection` even though the rows are hidden.
6. "Select all" (header tri-state → checked) selects all **visible** rows (not collapsed-
   hidden ones). When the user expands a parent and those children are not yet selected, the
   header checkbox becomes indeterminate.

**wave-3**

---

### §FS-2 Wave — editing mode behaviour (audit row 1)

#### §FS-2.1 Cell edit mode

Reference: Semantic §FS-2.1; mirrors DataGrid.Interaction.md §FS-1.5 cell-mode.

1. **Activate.** Double-clicking an editable cell (or pressing Enter when the cell has focus)
   activates an inline editor input. Other cells in the same row remain read-only.
2. **Commit.** Pressing Enter or moving focus out (blur) commits the value. `validate` is
   called; a non-null return blocks the commit and renders the error below the cell. On
   successful commit, `onItemChange({ dataItem, field, value })` fires.
3. **Cancel.** Pressing Escape abandons the edit; the cell reverts to its pre-edit value.
4. **Tab advance.** Pressing Tab while editing commits the current cell and activates the
   next editable cell in the row (wraps to first editable cell of the next row on last column).

**wave-4**

---

#### §FS-2.2 Row edit mode

Reference: Semantic §FS-2.1; mirrors DataGrid.Interaction.md §FS-1.5 row-mode.

1. Activating "Edit" switches all `column.editable === true` cells in the row to input mode.
2. Save / Cancel button pair appears in the row's last column (or a dedicated action column).
3. **Save.** Runs `validate` for each changed cell; all must pass. On success, `onItemChange`
   fires for each changed cell, then the row exits edit mode.
4. **Cancel / Escape.** Reverts all inputs to pre-edit values; the row exits edit mode. No
   callbacks fire.
5. Only one row is in edit mode at a time. Activating a second row implicitly cancels the
   first (no callback fires for the cancelled row).

**wave-4**

---

#### §FS-2.3 Batch mode overlay

Reference: Semantic §FS-2.1; mirrors DataGrid.Interaction.md §FS-1.5 batch-mode.

When `pendingChanges` + `onPendingChangesChange` are supplied:

1. Each committed cell edit updates `onPendingChangesChange` (accumulates) instead of firing
   `onItemChange` immediately.
2. A "Commit / Discard" bar appears above the TreeList when `pendingChanges` is non-empty.
3. "Commit" validates all pending cells; `onCommitChanges(pendingChanges)` fires on all-clear.
4. "Discard" fires `onDiscardChanges()` and clears all pending state.
5. Pending cells show a "dirty" visual indicator (left-accent border on the `<td>`).

**wave-4**

---

### §FS-3 Wave — keyboard navigation (closes G-TL1)

The M1 implementation has no keyboard navigation beyond the expand-button click. The full
WAI-ARIA `treegrid` pattern is introduced in wave-3 (Accessibility §FS-1) and the keyboard
matrix below matches that pattern.

| Key | Context | Behaviour | Wave tag |
|---|---|---|---|
| Tab / Shift+Tab | Focus inside TreeList | Move focus between interactive controls per natural tab order (header buttons, filter inputs if visible, row expand buttons, editable cells) | existing (natural) |
| Enter / Space | Header cell focused, `sortable` | Cycle sort for that column | wave-3 NEW |
| ArrowDown | Row or cell focused | Move focus to same column in the next visible row | wave-3 NEW (closes G-TL1) |
| ArrowUp | Row or cell focused | Move focus to same column in the previous visible row | wave-3 NEW (closes G-TL1) |
| ArrowRight | Row focused (first column) | If the row is collapsed and has children, expand the row. If already expanded, move focus to the first cell of the row. | wave-3 NEW |
| ArrowLeft | Row focused (first column) | If the row is expanded, collapse it. If collapsed or leaf, move focus to the parent row. | wave-3 NEW |
| Home | Cell focused | Move focus to the first cell in the current row | wave-3 NEW |
| End | Cell focused | Move focus to the last visible cell in the current row | wave-3 NEW |
| Enter | Editable cell focused | Open cell editor (`editMode: 'cell'`) | wave-4 NEW (requires §FS-2.1) |
| Escape | Cell editor active | Cancel edit; return focus to cell | wave-4 NEW |
| Escape | Row in edit mode | Cancel row edit; return focus to first editable cell in row | wave-4 NEW |

**Focus model (wave-3):**

- Each row `<tr>` has `tabIndex={0}` (roving tabIndex across rows, same model as WAI-ARIA
  treegrid). Exactly one row is the "active row" at a time (tabIndex={0}); all others are
  tabIndex={-1}.
- Individual cells `<td>` within the active row have `tabIndex={0}` only when the treegrid
  adopts the two-dimensional focus model (council gate — see Accessibility §FS-1.2).

**wave-3**
