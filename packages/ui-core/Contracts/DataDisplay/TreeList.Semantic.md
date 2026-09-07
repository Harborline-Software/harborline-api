# TreeList — Semantic Contract

- **Component:** TreeList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TreeList.Interaction.md) · [Accessibility](./TreeList.Accessibility.md) · [Styling](./TreeList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/TreeList.tsx`
- **Catalog row:** #141 TreeList (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled hierarchical list

---

## 1. Component purpose

**TreeList** — a table that displays hierarchical data using parent-child row relationships. Rows are expandable to reveal children. Columns are declared declaratively with optional custom cell renderers.

---

## 2. Props

```typescript
interface TreeListColumn<T> {
  field: keyof T & string
  title?: string
  width?: number | string
  cell?: (props: { dataItem: T; value: unknown }) => React.ReactNode
}

interface TreeListProps<T = Record<string, unknown>> {
  data: T[]
  columns: Array<TreeListColumn<T>>
  idField?: keyof T & string         // default: 'id'
  parentIdField?: keyof T & string   // default: 'parentId'
  expandField?: keyof T & string     // declared but not used in v1 impl
  expandedIds?: Array<string | number>   // controlled
  defaultExpandedIds?: Array<string | number>  // uncontrolled seed; default: []
  onExpandChange?: (id: string | number, expanded: boolean) => void
  className?: string
}
```

---

## 3. Tree flattening

Rows are built by `buildRows(data, null, 0)`: a recursive depth-first walk starting at root nodes (nodes whose `parentId === null || undefined`). Children of a node are included when their parent's id is in the `expanded` set. This produces a flat sorted array of `{ item, depth }` pairs.

---

## 4. Column rendering

First column (`ci === 0`) receives a left-padding indent of `12 + depth * 20` px. Columns with a `cell` prop receive `{ dataItem, value }` — otherwise the value is rendered as `String(value ?? '')`.

---

## 5. Expand toggle

First-column expand button: `aria-label={isExpanded ? 'Collapse' : 'Expand'}`. When the row has no children (`!hasChildren(id)`), the button is rendered with `visibility: hidden` (invisible but layout-present).

---

## 6. Controlled / uncontrolled

Controlled when `expandedIds` is provided. Uncontrolled uses `internalExpanded: Set<string|number>` seeded by `defaultExpandedIds`.

---

## 7. Known gaps

`expandField` prop is declared but not used — the implementation uses `expandedIds`/`defaultExpandedIds` instead.

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeList
audit rows 1-8. Where a capability is identical or near-identical to the DataGrid expansion
(Harborline #1022 pattern — DataGrid.Semantic.md §FS-*), this contract cross-references rather
than re-defines. FR-1/FR-2/FR-3 rulings apply (see
`_shared/design/polish/family-rulings-2026-06-11.md`).

---

### §FS-1 Wave — sorting + filtering + selection (audit rows 2, 3, 4)

#### §FS-1.1 Sorting (audit row 2)

Vocabulary mirrors DataGrid §FS-1.8 (multi-sort modifier-click). TreeList extends that
vocabulary with its own controlled pair.

```typescript
export interface TreeListSortDescriptor {
  field: string
  dir: 'asc' | 'desc'
}

// New props added to TreeListProps<T>:
sort?: TreeListSortDescriptor[]          // controlled; array for future multi-sort
onSortChange?: (sort: TreeListSortDescriptor[]) => void
sortable?: boolean                       // default false; enables clickable column headers
```

**Semantics:**

- When `sortable === true`, column header cells become interactive buttons. Clicking a header
  cycles through `unsorted → asc → desc → unsorted` for that column.
- `onSortChange` fires with the new `sort` array. Wave-3 supports **single-sort** (at most one
  entry in the array). Multi-sort modifier-click (Shift+click) is a wave-5 enhancement.
- `sort` is always controlled — there is no uncontrolled sort mode. When `sortable === true`
  but `sort` is omitted, the visual sort indicator is absent.
- The tree data model (parent-child relationships) is preserved under sorted data: root nodes
  are sorted among themselves; children within each parent group are sorted among themselves.
  This requires the caller to supply sort-aware data, or for the component to sort within
  groups (see §FS-1.1 client-side vs. server-side note below).
- **Client-side sort:** when `manualSort === false` (see prop table), TreeList sorts root nodes
  and each sibling group independently using the active `sort` descriptor. `buildRows` is
  called on the sorted data.
- **Server-side sort** (default, `manualSort === true`): caller re-supplies `data` in sorted
  order; TreeList does not reorder internally.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `sortable` | `boolean` | `false` | Enables clickable column headers with sort indicators. |
| `sort` | `TreeListSortDescriptor[]` | `[]` | Controlled sort state. |
| `onSortChange` | `(sort: TreeListSortDescriptor[]) => void` | — | Fires when the user toggles a sortable header. |
| `manualSort` | `boolean` | `true` | `false` = client-side sort within sibling groups; `true` = host supplies sorted data. |

**wave-3**

---

#### §FS-1.2 Filtering (audit row 3)

Vocabulary mirrors DataGrid §FS-1.2 (`FilterOperator`, `DataGridColumnFilter`). TreeList
reuses those exported types directly rather than defining parallel types.

```typescript
// Reuse from DataGrid (cross-reference: DataGrid.Semantic.md §FS-1.2):
import type { FilterOperator, DataGridColumnFiltersState } from '../DataDisplay/DataGrid'
// Alias for the TreeList context:
export type TreeListColumnFiltersState = DataGridColumnFiltersState

// New props:
filterable?: boolean                                 // default false
columnFilters?: TreeListColumnFiltersState
onColumnFiltersChange?: (filters: TreeListColumnFiltersState) => void
manualFiltering?: boolean                            // default true (server-side)
```

**Semantics:**

- When `filterable === true`, each column header shows a filter input row below the header.
  This mirrors DataGrid's `showFilterRow` (Polish-pilot).
- `columnFilters` + `onColumnFiltersChange` are the controlled filter pair. Presence enables
  the operator-menu filter model (funnel icon per column — same as DataGrid §FS-1.2).
- **Client-side filtering** (`manualFiltering === false`): rows are excluded from `buildRows`
  when they do not match the active filters. **Branch nodes are included if any descendant
  matches** (parent-visible-on-child-match semantics standard for tree filtering).
- **Server-side filtering** (default, `manualFiltering === true`): the caller re-supplies
  `data` with pre-filtered rows; TreeList fires `onColumnFiltersChange` only.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `filterable` | `boolean` | `false` | Renders a filter input row below column headers. |
| `columnFilters` | `TreeListColumnFiltersState` | `[]` | Controlled per-column filter state. |
| `onColumnFiltersChange` | `(filters: TreeListColumnFiltersState) => void` | — | Fires when any column filter changes. |
| `manualFiltering` | `boolean` | `true` | `false` = client-side; `true` = host handles filtering. |

**wave-3**

---

#### §FS-1.3 Selection (audit row 4)

Vocabulary mirrors DataGrid §FS-1.3 (`selectionMode`, `RowSelectionState`). The TreeList
selection model is row-based (by the `idField` value).

```typescript
// Reuse from DataGrid (cross-reference: DataGrid.Semantic.md §FS-1.3):
export type TreeListSelectionMode = 'none' | 'single' | 'multiple'

// Row selection state — { [rowId]: boolean } — same shape as DataGrid.RowSelectionState
export type TreeListRowSelectionState = Record<string, boolean>

// New props:
selectionMode?: TreeListSelectionMode        // default 'none'
rowSelection?: TreeListRowSelectionState
onRowSelectionChange?: (state: TreeListRowSelectionState) => void
// selectedField: data-driven selection (Kendo-style field name on the data object)
selectedField?: keyof T & string            // optional; when supplied, row.selected truthy = selected
```

**Semantics:**

- `'none'` (default) — no selection affordance. M1 behaviour.
- `'single'` — clicking a data row sets `rowSelection` to `{ [rowId]: true }` and clears all
  others. No checkbox column is rendered; the row gets a selected background token.
- `'multiple'` — a leading checkbox column is prepended (same as DataGrid's `__select__`
  column). Header tri-state checkbox selects/deselects all visible rows. `onRowSelectionChange`
  fires with the full next map.
- `selectedField`: an alternative data-driven path. When supplied, TreeList reads
  `row[selectedField]` as a boolean to determine initial selection. Changes still fire
  `onRowSelectionChange`.
- Expand/collapse does not affect selection state — collapsed children retain their selection
  state; they are just hidden from the DOM.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `selectionMode` | `TreeListSelectionMode` | `'none'` | Row selection model. |
| `rowSelection` | `TreeListRowSelectionState` | — | Controlled selection state. |
| `onRowSelectionChange` | `(state: TreeListRowSelectionState) => void` | — | Fires when selection changes. |
| `selectedField` | `keyof T & string` | — | Data-field path for initial selection state. |

**wave-3**

---

### §FS-2 Wave — editing modes (audit row 1)

#### §FS-2.1 Editing modes

Vocabulary mirrors DataGrid §FS-1.5 (`editMode`, `validate`, `pendingChanges`). TreeList
reuses those exported types directly.

```typescript
// Reuse from DataGrid (cross-reference: DataGrid.Semantic.md §FS-1.5):
export type TreeListEditMode = 'cell' | 'row' | 'dialog'
export type TreeListCellValidator<T> = (row: T, field: string, value: string) => string | null
export type TreeListPendingChanges = Record<string, Record<string, string>>

// Column-level additions:
interface TreeListColumn<T> {
  field: keyof T & string
  title?: string
  width?: number | string
  cell?: (props: { dataItem: T; value: unknown }) => React.ReactNode
  // New — editing:
  editable?: boolean                          // default false
  editor?: 'text' | 'numeric' | 'date' | 'boolean'  // default 'text' when editable
}

// New props on TreeListProps<T>:
editMode?: TreeListEditMode                  // default 'cell'
editField?: keyof T & string               // data field marking a row as in-edit (Kendo-style)
validate?: TreeListCellValidator<T>
pendingChanges?: TreeListPendingChanges
onPendingChangesChange?: (changes: TreeListPendingChanges) => void
onCommitChanges?: (changes: TreeListPendingChanges) => void
onDiscardChanges?: () => void
onItemChange?: (event: { dataItem: T; field: string; value: unknown }) => void
renderEditDialog?: (row: T, onSave: (changes: Record<string, string>) => void, onClose: () => void) => React.ReactNode
```

**Semantics:**

- `column.editable === true` marks a column as editable. Non-editable columns remain read-only
  in all edit modes.
- `column.editor` determines the input type: `'text'` (default), `'numeric'` (number input),
  `'date'` (date picker), `'boolean'` (checkbox toggle).
- **`editMode: 'cell'`** — double-clicking or pressing Enter on a focused editable cell
  activates an inline editor. Commit on Enter/blur fires `onItemChange({ dataItem, field, value })`.
  Escape cancels.
- **`editMode: 'row'`** — activating an "Edit" action (last column or `rowActions`) switches the
  entire row into edit mode. All `column.editable === true` cells become inputs. Save/Cancel
  buttons appear in the row. Semantics mirror DataGrid §FS-1.5 row-mode.
- **`editMode: 'dialog'`** — activating "Edit" opens `renderEditDialog(row, onSave, onClose)`.
  Semantics mirror DataGrid §FS-1.5 dialog-mode.
- `editField`: Kendo-compatibility path. When supplied, the component reads `row[editField]`
  as a boolean; `true` means the row is currently in edit mode. Callers update this field to
  programmatically enter/exit row edit mode.
- `onItemChange` is the per-cell-change event (Kendo vocabulary; DataGrid uses `onCellEdit`).
  Both emit the same information; `onItemChange` is the canonical TreeList name.
- Batch mode (`pendingChanges` + `onPendingChangesChange`) works identically to DataGrid
  §FS-1.5 batch mode — changes accumulate, a commit/discard bar appears.
- **Hierarchical note:** editing does NOT propagate to children. Editing a parent row changes
  only the parent's fields.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `editMode` | `TreeListEditMode` | `'cell'` | Edit interaction model. |
| `editField` | `keyof T & string` | — | Data-field marking a row as currently in edit mode. |
| `validate` | `TreeListCellValidator<T>` | — | Per-cell validator; non-null blocks commit. |
| `onItemChange` | `(event: { dataItem: T; field: string; value: unknown }) => void` | — | Fires on cell-level change. |
| `pendingChanges` | `TreeListPendingChanges` | — | Controlled batch-edit state. |
| `onPendingChangesChange` | `(changes) => void` | — | Fires on each edit in batch mode. |
| `onCommitChanges` | `(changes) => void` | — | Fires on batch commit. |
| `onDiscardChanges` | `() => void` | — | Fires on batch discard. |
| `renderEditDialog` | `(row, onSave, onClose) => ReactNode` | — | Required for `'dialog'` mode. |

**wave-4**

---

### §FS-3 Wave — locked columns + aggregates + pager (audit rows 5, 6, 7, 8)

#### §FS-3.1 Locked (frozen) columns (audit row 7)

Vocabulary mirrors DataGrid §FS-* pinned columns (column-pinning is tracked as part of the
DataGrid column-resize/reorder wave). TreeList's simpler model uses a per-column `locked`
boolean.

```typescript
// Column-level addition:
interface TreeListColumn<T> {
  // ... existing fields + editable/editor from §FS-2.1 ...
  locked?: boolean    // default false; pins this column to the left
}
```

**Semantics:**

- Columns with `locked === true` are rendered first (leftmost) regardless of their position in
  the `columns` array, and become `position: sticky; left: <offset>px; z-index: 1` within the
  scrolling table.
- The depth-indent (`paddingLeft: 12 + depth * 20 px`) still applies to the first locked
  column (the tree structure column is expected to be the first locked column).
- Locked columns are not reorderable or resizable in wave-5.

**wave-5**

---

#### §FS-3.2 Aggregates (footer rows) (audit row 5)

Vocabulary mirrors DataGrid §FS-1.7 (footer cells) and §FS-2.1 (aggregates). TreeList
renders a `<tfoot>` summary row at the bottom of the visible data.

```typescript
export type TreeListAggregateFunction = 'sum' | 'avg' | 'min' | 'max' | 'count'

export interface TreeListColumnAggregate {
  fn: TreeListAggregateFunction
  label?: string    // optional prefix (e.g. "Total:"); defaults to fn name
}

// New column-level field:
interface TreeListColumn<T> {
  // ... existing fields ...
  aggregate?: TreeListColumnAggregate
}

// New prop:
showAggregates?: boolean   // default false; renders a <tfoot> row with computed values
```

**Semantics:**

- When `showAggregates === true` and at least one column declares `aggregate`, TreeList renders
  a `<tfoot>` row below the body.
- Aggregate values are computed over **visible** rows only (rows currently expanded in the
  tree) when `manualFiltering === false`. In server-side mode (`manualFiltering === true`), the
  caller pre-calculates and supplies aggregate values via a `footerData?: Record<string,
  unknown>` prop (each key = `column.field`; value = pre-calculated aggregate).
- `footerData` overrides client-side computed aggregates when both are present.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `showAggregates` | `boolean` | `false` | Renders a tfoot aggregate row. |
| `footerData` | `Record<string, unknown>` | — | Pre-computed aggregate values (server-side). |

**wave-5**

---

#### §FS-3.3 Pager integration (audit row 8)

Vocabulary mirrors DataGrid §FS-1.1 (`PaginationState`, `onPaginationChange`).

```typescript
// Reuse from DataGrid (cross-reference: DataGrid.Semantic.md §FS-1.1):
import type { PaginationState } from './DataGrid'

// New props:
pageable?: boolean                                   // default false
pagination?: PaginationState                         // { pageIndex, pageSize }
onPaginationChange?: (state: PaginationState) => void
pageSizeOptions?: number[]                           // default [10, 25, 50]
rowCount?: number                                    // total rows for server-side page calculation
manualPagination?: boolean                           // default true
```

**Semantics:**

- When `pageable === true` and `pagination` is supplied, TreeList renders a built-in Pager
  footer (reuses the sibling Pager component).
- `manualPagination === true` (default): the host re-supplies `data` with the correct page
  slice. `onPaginationChange` fires on page/size change.
- `manualPagination === false`: TreeList slices `data` locally using `buildRows` output
  (paginating the flat visible-row list, not the raw `data` array — so pagination operates on
  visible/expanded rows, preserving tree structure per page).
- **Tree boundary note:** a row's children are never split across pages — if a parent row is on
  page N, its visible children appear on the same page even if they overflow the page size.
  This may result in pages that show fewer rows than `pageSize` when expanded subtrees push
  items to later pages. This is the expected UX for tree pagination.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `pageable` | `boolean` | `false` | Enables built-in pager footer. |
| `pagination` | `PaginationState` | — | Controlled page state. |
| `onPaginationChange` | `(state: PaginationState) => void` | — | Fires on page/size change. |
| `pageSizeOptions` | `number[]` | `[10, 25, 50]` | Page size choices. |
| `rowCount` | `number` | — | Total row count for server-side page calculation. |
| `manualPagination` | `boolean` | `true` | `false` = client-side paging; `true` = host supplies page slice. |

**wave-5**

---

#### §FS-3.4 Column resize + reorder (audit row 6)

Deferred to wave-5. Vocabulary and implementation will mirror DataGrid §FS-1.4 (column
reorder) and the DataGrid Polish-pilot column-resize spec. Per-column `locked` (§FS-3.1) takes
precedence over reorder for locked columns.

**wave-5 (spec deferred — mirrors DataGrid §FS-1.4 when specced)**
