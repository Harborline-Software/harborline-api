# DataGrid — Interaction Contract

- **Component:** DataGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataGrid.Semantic.md) · [Styling](./DataGrid.Styling.md) · [Accessibility](./DataGrid.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/DataGrid.tsx`
- **Catalog row:** #35 DataGrid (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract describes **how DataGrid behaves in response to user input** — the
state transitions, the callbacks it emits, and the boundary between the
controlled host (default) and engine-local behaviour. It references the prop and
event shapes defined in the [Semantic contract](./DataGrid.Semantic.md); it
does not restate them. Visual styling and ARIA wiring are owned by the Styling
and Accessibility contracts (PAO).

**Default operation mode is host-controlled.** Unless the host omits the
controlled-state props, DataGrid mirrors the host's `sorting` / `rowSelection`
state and emits updates via the controlled callbacks. The host owns the data
fetch / sort / paginate operation; DataGrid owns the user-input → callback
translation.

---

## 2. Sort

- **Trigger:** activating a sortable column header — click, or Enter/Space when
  the header's sort button is focused. Sortability is set per column via
  `ColumnDef.enableSorting` (default `true`; the synthetic `__select__` column
  is `enableSorting: false`).
- **Direction cycle:** `unsorted → asc → desc → unsorted` on successive
  activations of the same header (TanStack default).
- **Indicator:** the active sort column's header shows `↑` (asc, `ChevronUp`) or
  `↓` (desc, `ChevronDown`). Sortable non-active columns show a neutral
  `ChevronsUpDown` affordance to communicate sortability.
- **Single-sort baseline:** only **one** column is sorted at a time. Activating
  a different column's header clears the prior column's sort and starts that
  column at its **auto sort direction** — `asc` for string columns, `desc` for
  numeric/date columns (TanStack v8 `getAutoSortDir()` behavior). This deviates
  slightly from DataGrid §2 "starts at `asc`"; hosts that need a uniform
  ascending-first start should set `sortDescFirst: false` on each `ColumnDef`.
- **Callback:** each transition fires `onSortingChange(updater)` where `updater`
  is either the next `SortingState` or a `(prev) => next` function. Host code
  must accept both forms.
- **Server-side default:** the engine is configured with `manualSorting: true`,
  so DataGrid does **not** reorder rows locally. The host listens to
  `onSortingChange`, re-queries with the new sort, and supplies new `data`.
  Pure-client-side sort is a deferred enhancement (Semantic §7).

---

## 3. Filter

DataGrid does **not** ship a filter UI in M1. The Semantic contract notes the
host composes filtering via a ListToolbar above the table; DataGrid receives
filtered `data` from the host.

This contract therefore has no filter trigger / debounce / clear behaviour to
specify at the DataGrid layer. Filter-input behaviour belongs to the
ListToolbar's own contract (future wave).

**Superseded for waves 3-5:** The Polish pilot (`showFilterRow`) adds a client-side
contains-filter row. Wave-3 (`columnFilters` + `onColumnFiltersChange`) adds the
full operator-menu filter model — see §FS-1.2 below.

---

## 4. Row selection

Selection behaviour depends on whether **both** `rowSelection` and
`onRowSelectionChange` are supplied (Semantic §3.2 "selection-enabled mode").

### Selection disabled (no controlled props)

- The leading select column is **not rendered**.
- The bulk-actions toolbar is **not rendered**.
- Row hover styling is present (`hover:bg-gray-50`), but row click does not fire
  a selection or activation event in M1 (see Semantic §4 — `onRowClick` is
  deferred).

### Selection enabled (multiple-mode, M1 baseline)

The shipping implementation is effectively **multiple-mode** — the synthetic
select column shows per-row checkboxes plus a header tri-state checkbox. A
single-mode is not yet exposed (Semantic open question #1).

- **Row-checkbox toggle:** clicking a row checkbox toggles that row in/out of
  the selection. `onRowSelectionChange` fires with the next `RowSelectionState`
  map (full next state, not a delta).
- **Header tri-state checkbox:** click toggles all currently-visible rows.
  - Unchecked → checked: selects all rows on the current page.
  - Checked → unchecked: clears the entire selection.
  - The header checkbox renders `indeterminate` (visual only) when some — but
    not all — rows are selected.
- **Selected-row highlight:** rows in the selection set carry a `bg-blue-50`
  treatment (visual; full token surface owned by PAO Styling).
- **Bulk-actions toolbar:** rendered above the table when the selection set is
  non-empty. Shows `"{count} selected"` (announced via `aria-live="polite"`),
  the host-supplied `bulkActions` content, and a "Clear" button that calls
  `table.resetRowSelection()` (which fires `onRowSelectionChange({})`).

### Stable row identity

Controlled selection requires a stable row id across re-renders. The host MUST
supply `getRowId` when using controlled selection; without it, the column
engine falls back to row index and selection state will drift if `data` is
reordered or filtered.

---

## 5. Pagination

DataGrid does **not** render a Pager in M1. Hosts wire pagination by placing
the sibling [Pager](./Pager.Semantic.md) below DataGrid and feeding paginated
`data`. See [Pager.Interaction.md](./Pager.Interaction.md) for that
component's behaviour.

**Superseded for wave-3:** The `pagination` + `onPaginationChange` props add a
built-in pager footer — see §FS-1.1 below.

---

## 6. Loading state

- **Trigger:** `isLoading === true`.
- **Presentation:** the table body is **replaced** with ~6 skeleton rows
  (`SKELETON_ROW_COUNT = 6`). Each skeleton cell renders a pulsing rounded grey
  bar (`animate-pulse rounded bg-gray-100`). Column headers remain visible and
  in their normal sortable / non-sortable state.
- **Interaction disablement:** while loading, the data rows are absent, so row
  selection and row-click interactions cannot fire. Column-header **sort
  buttons remain interactive** — clicking them still emits
  `onSortingChange`. This is a deliberate UX choice: hosts can drive
  paginated queries during initial load by toggling sort.
  - _Deviates from DataGrid §6 "all grid interactions are disabled"._ The
    council should confirm whether headers should be visually-disabled
    (e.g. opacity / cursor:not-allowed) during loading, or fully click-blocked,
    or remain as-is.
- **Bulk-actions toolbar:** if a selection survives into the loading state, the
  toolbar remains visible above the skeleton rows (the toolbar reads from
  `rowSelection`, not from `table.getRowModel()`). Hosts who want the toolbar
  to disappear during loading can clear selection on the load-start callback.
- **Exit:** when `isLoading` returns to `false`, the table body renders the
  current `data` and normal interaction resumes.

---

## 7. Keyboard navigation

The M1 DataGrid implementation does **not** wire grid-pattern keyboard
navigation (arrow-key cell focus, Enter activation, Tab into/out of cells).
Keyboard focus follows standard tab order through interactive descendants:

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Moves focus between interactive controls in tab order: header sort-buttons (left → right), then bulk-actions toolbar controls (if visible), then row checkboxes (top → bottom), then cell-embedded interactive controls within rows. |
| Enter / Space (on sort button) | Toggles the column's sort (next step in the cycle). |
| Space (on row checkbox) | Toggles that row's selection. |
| Space (on header checkbox) | Toggles "select all on page". |

Grid-pattern keyboard navigation (DataGrid §7) is a **deferred enhancement**.
Per-cell focus + arrow-key navigation is owned by the Accessibility contract
(PAO) and will arrive in a future wave.

---

## 8. Empty state

- **Trigger:** `data.length === 0` **and** `isLoading === false`.
- **Presentation:** the table body shows a single full-width row with
  `colSpan={effectiveColumns.length}` containing either the `emptyState` slot
  (when supplied) or the adapter default `<p>No results.</p>` placeholder.
- An empty state while `isLoading === true` is **not** shown — the skeleton-row
  loading affordance (§6) takes precedence.
- The bulk-actions toolbar is hidden in the empty state because selection is
  necessarily empty (no rows to select).

---

## 9. Interaction-state precedence

When multiple conditions hold, resolve in this order (highest precedence first):

1. **Loading** (`isLoading === true`) — skeleton rows; row-level interactions
   absent; header sort interactions remain (§6, council to confirm).
2. **Empty** (`data.length === 0`, not loading) — empty state shown; bulk-
   actions toolbar hidden.
3. **Populated with selection** — normal rows; selection-enabled column +
   bulk-actions toolbar visible when selection is non-empty.
4. **Populated without selection mode** — normal rows; no selection column;
   no bulk-actions toolbar.

---

## 10. Council open questions (Interaction)

1. **Loading-state header behaviour.** Should column-header sort buttons be
   disabled during `isLoading`, matching DataGrid §6 "all grid interactions
   are disabled"? Or is the current "headers stay interactive" treatment
   intentional? (Leaning: align with DataGrid §6 — disable headers while
   loading; confirm before changing the implementation.)
2. **Row activation event.** Should DataGrid expose `onRowClick(row,
   rowIndex)` to match DataGrid §4? The shipping component has the visual
   affordance (`hover:bg-gray-50`) but no event surface. Adding it now would
   close the DataGrid-vs-DataGrid shape gap.
3. **Single-select mode.** Add an explicit `selectable: 'none' | 'single' |
   'multiple'` prop (mirroring DataGrid §3.2)? The current implicit-from-
   props pattern works but loses expressiveness for single-select use cases
   common in detail-pane workflows.

---

## Polish expansion (2026-06-11 — Polish-pilot, see _shared/design/polish-gate.md)

Reference: SVAR React DataGrid. REQUIRED additions (virtualization, undo,
CSV export, tree data stay OUT of scope):

1. **Column resize.** Drag handle on header cell edge resizes the column
   (min 60px); double-click handle auto-fits. Widths controlled-optional via
   `onColumnResize`.
2. **Column pinning.** `pinnedColumns?: string[]` pins columns to the start
   (sticky within horizontal scroll, elevated background).
3. **Inline editing.** `editable` columns (per-column `editor: 'text' |
   'select'` with options) enter edit mode on double-click or Enter; commit
   on Enter/blur → `onCellEdit(row, columnId, value)`; Escape cancels.
4. **Header filter row.** `showFilterRow` renders per-column text filters
   (client-side contains-match via the existing tanstack pipeline).
5. **Keyboard cell navigation.** Arrow keys move a visible cell focus ring;
   Enter activates edit on editable cells (first slice of G-DGA9).
6. **Context menu.** Right-click on a row opens a menu fed by
   `rowActions?: Array<{ label, onSelect(row), destructive? }>`.
7. **Visual pass.** Sticky header, row hover wash, optional `zebra`,
   stronger sort indicators, 24px checkbox touch targets (closes G-DGA6).

---

## Full-surface expansion (2026-06-11 — waves 3-5, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope:** Behaviour specifications for every PARTIAL / MISSING capability introduced in
Semantic §FS-*. Prop shapes are owned by Semantic; this section owns the user-visible state
transitions, the edge cases, and the keyboard extensions. Visual styling and ARIA wiring are
owned by Styling §FS and Accessibility §FS respectively.

---

### §FS-1 Wave-3 — core table UX

#### §FS-1.1 Pagination behaviour

Reference: Semantic §FS-1.1.

1. **Page-index change.** Clicking a page button in the built-in pager fires
   `onPaginationChange({ pageIndex: n, pageSize: current })`. DataGrid sets `isLoading`
   state is the host's responsibility during the transition; no internal loading interlock.
2. **Page-size change.** Changing the size selector fires
   `onPaginationChange({ pageIndex: 0, pageSize: newSize })` — page resets to 0 on size change.
3. **Boundary clamping.** The built-in pager disables the "previous" button when
   `pageIndex === 0` and the "next" button when `pageIndex >= pageCount - 1`. The "next" button
   is always enabled when `rowCount` is unknown (omitted).
4. **Client-side mode** (`manualPagination === false`): the engine slices `data` locally;
   `onPaginationChange` still fires but the host is not required to re-query.
5. **Keyboard.** Within the pager, Tab navigates between page buttons and the size selector.
   Enter / Space activate the focused control. PageUp / PageDown shortcuts (when grid focus is
   inside the pager region) navigate one page — see §FS-3.5 for the full keyboard matrix.

**wave-3**

---

#### §FS-1.2 Filtering operator menu flow

Reference: Semantic §FS-1.2.

1. **Operator button.** Each column's filter input shows a funnel icon button (left of the
   input). Clicking it opens the operator-selection menu. The menu is a `role="menu"` overlay
   (same pattern as `ColumnMenu`).
2. **Operator selection.** Clicking a menu item sets the operator for that column and closes the
   menu. `onColumnFiltersChange` fires with the updated filter entry. If the column had no prior
   value, the value remains empty until the user types.
3. **`isnull` operator.** Selecting `isnull` hides the value input (no text entry needed);
   `onColumnFiltersChange` fires immediately with `{ id, operator: 'isnull', value: '' }`.
4. **Clear filter.** A "Clear" item at the bottom of each operator menu removes the filter for
   that column. `onColumnFiltersChange` fires with the column's filter entry removed.
5. **Filter-in-column-menu.** When `showColumnMenu` is `true` and `columnFilters` is active, the
   column menu (kebab) includes a "Filter…" item that opens a compact operator+value popover
   inline in the column header. Closing the popover commits the current operator + value.
6. **Debounce.** The value input fires `onColumnFiltersChange` on `change`; the host is
   responsible for debouncing server-side queries. DataGrid does NOT debounce internally.
7. **Escape.** Pressing Escape while the operator menu is open closes the menu without changing
   the operator. Pressing Escape while the value input is focused clears the input value and
   fires `onColumnFiltersChange` with `value: ''` for that column.

**wave-3**

---

#### §FS-1.3 Selection mode behaviour

Reference: Semantic §FS-1.3.

**Single mode (`selectionMode === 'single'`):**

1. Clicking a row (anywhere outside an interactive cell control) sets `rowSelection` to
   `{ [rowId]: true }` and clears all other entries. `onRowSelectionChange` fires with the new
   single-entry map.
2. Clicking the already-selected row deselects it; `onRowSelectionChange` fires with `{}`.
3. Shift+click has no multi-select effect in single mode — it behaves identically to a plain click.
4. No header-checkbox is rendered in single mode.
5. `onRowClick` fires additionally (after the selection update) for single-mode clicks.

**Multiple mode — row-click activation (new in wave-3):**

6. When `onRowClick` is supplied, clicking a data row (outside the checkbox cell) fires
   `onRowClick(row.original, event)` **without** altering selection state. Selection is still
   controlled exclusively through the checkbox column.
7. Shift+click does not trigger multi-range selection in wave-3 (deferred to a later patch).

**wave-3**

---

#### §FS-1.4 Column drag-reorder behaviour

Reference: Semantic §FS-1.4.

1. **Drag start.** The user presses and holds the pointer on a reorderable header cell. A drag
   ghost (semi-transparent copy of the header) appears at the cursor.
2. **Drop indicator.** As the ghost moves over other header cells, a vertical drop-indicator line
   appears between columns to show the insertion position.
3. **Drop.** Releasing the pointer fires `onColumnOrderChange` with the new `ColumnOrderState`.
   The ghost disappears; the table re-renders with the new column order.
4. **Non-reorderable columns.** Pinned columns and `__select__` / `__expand__` synthetic columns
   are not draggable and do not show a drop indicator when a ghost passes over them.
5. **Keyboard reorder.** Not supported in wave-3 (pointer-only UX).
6. **Cancel.** Pressing Escape while dragging cancels the reorder; the column order reverts to the
   pre-drag state and no callback fires.
7. **Touch support.** Not required in wave-3.

**wave-3**

---

#### §FS-1.5 Editing mode flows

Reference: Semantic §FS-1.5.

**Row mode (`editMode === 'row'`):**

1. The "Edit" affordance is a per-row action button (rendered in the last column or via
   `rowActions`). Activating it switches the row to edit mode: all editable cells become inputs
   simultaneously. Other rows remain uneditable.
2. **Save.** Clicking "Save" (or pressing Enter when focus is inside any row-edit input) runs
   `validate` for each changed cell. If all cells validate, `onCellEdit` fires for each changed
   cell, then the row exits edit mode. If any cell fails validation, errors render inline and the
   row stays in edit mode.
3. **Cancel.** Clicking "Cancel" (or pressing Escape) exits edit mode without firing any
   callbacks; all input values revert to their pre-edit values.
4. Only one row is in edit mode at a time. Activating "Edit" on a second row while another is
   being edited implicitly cancels the first row's edit (no callback fires for the cancelled row).

**Dialog mode (`editMode === 'dialog'`):**

5. The "Edit" affordance opens the dialog supplied by `renderEditDialog(row, onSave, onClose)`.
6. The dialog's `onSave(changes)` callback receives a `Record<string, string>` of changed fields.
   DataGrid calls `validate` per changed field; a non-null return is passed back to `onSave` as
   an error map (the dialog is responsible for surfacing these to the user).
7. `onSave` is only called when all validations pass; `onCellEdit` fires for each changed field
   after `onSave`.
8. `onClose` dismisses the dialog without firing callbacks.

**Batch mode (overlays cell or row mode):**

9. When `pendingChanges` + `onPendingChangesChange` are supplied, each cell edit updates
   `pendingChanges` via `onPendingChangesChange` instead of immediately firing `onCellEdit`.
10. A "Commit N changes" / "Discard" bar appears above the grid (above the toolbar band if
    present) when `pendingChanges` is non-empty.
11. Clicking "Commit" runs `validate` for every pending cell. All must pass; partial commit is
    not supported. On success, `onCommitChanges(pendingChanges)` fires; `onPendingChangesChange`
    fires with `{}`.
12. Clicking "Discard" fires `onDiscardChanges()`; `onPendingChangesChange` fires with `{}`.
13. Navigating away from the page / component unmounting does NOT auto-commit; the host is
    responsible for guarding against accidental loss.

**wave-3**

---

### §FS-2 Wave-4 — structure

#### §FS-2.1 Grouping expand/collapse + aggregate recompute

Reference: Semantic §FS-2.1.

1. **Group row.** Each group renders a `<tr>` with a ChevronRight / ChevronDown icon (collapsed /
   expanded) and the group-key label. Clicking the row (or the chevron, or pressing Enter/Space
   on the group row) toggles the group's expanded state.
2. **Collapse.** A collapsed group hides its child data rows (and any nested group rows). The
   aggregate cells in the group row remain visible.
3. **Expand.** An expanded group shows its immediate child rows. Nested groups are rendered
   collapsed by default on first expansion.
4. **Aggregate recompute.** Aggregate values in group-row cells reflect the current set of visible
   child rows only if `manualFiltering === false` (client-side). With `manualFiltering === true`
   (server-side), the host is responsible for passing correct aggregate values through a custom
   `renderGroupRow` renderer.
5. **"Expand all / Collapse all".** Not a built-in DataGrid control in wave-4; hosts may
   programmatically set `expanded: true` (TanStack convention for "all expanded") via the
   controlled `expanded` prop (shared with detail rows §FS-2.2 but scoped to group rows).
6. **Empty group.** A group with zero child rows after filtering renders with its label and an
   "(0)" count or no count (configurable via `renderGroupRow`). It does NOT show an expand icon.

**wave-4**

---

#### §FS-2.2 Detail row toggle behaviour

Reference: Semantic §FS-2.2.

1. **Chevron column.** The `__expand__` synthetic column prepends a chevron icon button. Clicking
   it (or pressing Enter/Space when focused) toggles that row's expanded state.
2. **Expanded state.** When a row is expanded, a detail `<tr>` is inserted immediately below the
   data row. The detail row contains a single `<td colSpan={effectiveColumns.length}>` with the
   result of `renderDetail(row.original)`.
3. **Collapse.** Clicking the chevron again (or pressing Enter/Space) collapses the detail row
   and removes the detail `<tr>` from the DOM.
4. **Multiple expansions.** Multiple rows can be expanded simultaneously. There is no
   single-expand-at-a-time mode in wave-4.
5. **Keyboard.** Enter/Space on the chevron cell toggles. Arrow keys move focus between the
   chevron cell and adjacent data cells normally. Tab into the expanded detail region follows
   natural document order (focus enters the host's rendered detail content).
6. **Row selection + detail rows.** The selection checkbox appears in both the data row and
   — if the `__select__` column is present — the detail row does NOT show a checkbox (the detail
   row is not independently selectable).

**wave-4**

---

#### §FS-2.3 Row pinning behaviour

Reference: Semantic §FS-2.3.

1. **Pinned-top rows** render as the first rows in `<tbody>` separated from center rows by a
   visually elevated border. They scroll with the column header (they are NOT sticky in wave-4 —
   sticky pinned rows are a wave-5 enhancement).
2. **Pinned-bottom rows** render as the last rows before the footer / pager.
3. **Selection.** Pinned rows participate in selection normally; the header tri-state checkbox
   includes pinned rows in its "all selected" calculation.
4. **Sorting.** Pinned rows are excluded from sort reordering; they stay at the top/bottom
   regardless of the active sort.

**wave-4**

---

### §FS-3 Wave-5 — platform

#### §FS-3.1 Toolbar slot behaviour

Reference: Semantic §FS-3.1.

1. The toolbar band renders as a `<div role="toolbar" aria-label="Grid toolbar">` above the
   bulk-actions bar. It has no default content.
2. When `toolbar` is `null` or `undefined`, no toolbar band DOM element is rendered.
3. The toolbar band does NOT suppress the bulk-actions bar — both are visible simultaneously
   when selection is active and `toolbar` is supplied.
4. Arrow-key navigation inside the toolbar follows the WAI-ARIA toolbar pattern (see
   Accessibility §FS-3.1).

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.2 Global search behaviour

Reference: Semantic §FS-3.2.

1. DataGrid applies the `globalFilter` value to TanStack's global filter pipeline when
   `manualFiltering === false`. The filter tests every column's cell value for the string
   (case-insensitive contains by default — TanStack `globalFilterFn: 'includesString'`).
2. Debounce is the host's responsibility. DataGrid fires `onGlobalFilterChange` synchronously
   on each input event from the host's `SearchInput`.
3. When `globalFilter` is non-empty and `columnFilters` is also active, both are applied as an
   AND conjunction (TanStack default).
4. Clearing `globalFilter` to `''` removes the global filter.

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.5 Keyboard matrix expansion

The existing arrow-key navigation (wave-1/2 Polish) is extended in wave-5. The full matrix,
including entries already shipped, is recorded here for completeness. Entries marked
`[wave-5 NEW]` are additions; unmarked entries are existing behaviours.

| Key | Context | Behaviour | Wave tag |
|---|---|---|---|
| ArrowRight | Cell focused | Move focus one cell right (clamp at last column). | existing |
| ArrowLeft | Cell focused | Move focus one cell left (clamp at first column). | existing |
| ArrowDown | Cell focused | Move focus one cell down (clamp at last row). | existing |
| ArrowUp | Cell focused | Move focus one cell up (clamp at first row). | existing |
| Enter | Cell focused (editable) | Open cell editor (cell mode) or begin row edit (row mode). | existing |
| Enter | Group row focused | Toggle group expand/collapse. | wave-5 NEW (requires §FS-2.1) |
| Enter | Expand chevron cell | Toggle detail row. | wave-5 NEW (requires §FS-2.2) |
| Escape | Cell editor active | Cancel edit; return focus to cell. | existing |
| Escape | Row in edit mode | Cancel row edit; return focus to the row's first editable cell. | wave-5 NEW (requires §FS-1.5) |
| PageDown | Cell focused | Move focus to the same column in the last row of the current page, OR advance one page if at the last row of the current page. | wave-5 NEW |
| PageUp | Cell focused | Move focus to the same column in the first row of the current page, OR go back one page if at the first row of the current page. | wave-5 NEW |
| Home | Cell focused | Move focus to the first cell of the current row. | wave-5 NEW |
| End | Cell focused | Move focus to the last visible cell of the current row. | wave-5 NEW |
| Ctrl+Home | Cell focused | Move focus to the first cell of the first row (page 0 if paginated). | wave-5 NEW |
| Ctrl+End | Cell focused | Move focus to the last cell of the last row (last page if paginated). | wave-5 NEW |
| Tab | Inside toolbar | Move focus to the next toolbar control. | wave-5 NEW (requires §FS-3.1) |
| Shift+Tab | Inside toolbar | Move focus to the previous toolbar control. | wave-5 NEW (requires §FS-3.1) |

**Council gate G-DGA1 dependency.** PageUp/PageDown and Home/End behaviours above are defined
for the `table` pattern (focusable `<td tabIndex={-1}>`). If the council adopts `role="grid"`,
these keys must additionally comply with the WAI-ARIA grid composite pattern — the final key
bindings are confirmed post-council. See Accessibility §FS-3.5 for both paths.

**wave-5**
