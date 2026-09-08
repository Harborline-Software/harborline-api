# DataGrid — Semantic Contract

- **Component:** DataGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DataGrid.Interaction.md) · [Styling](./DataGrid.Styling.md) · [Accessibility](./DataGrid.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/DataGrid.tsx`
- **Catalog row:** #35 DataGrid (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled table (no Radix primitive)

---

## 1. Purpose

DataGrid is the React-track tabular data-display surface of the design system. It
realises the framework-neutral DataGrid contract for the React adapter (ADR 0107
L2 + L3) and is the component that ships in `@harborline-software/ui-react` today.

This contract describes the **public shape and meaning** of the component as it
exists on `origin/main`. It is a **reverse-spec**: it describes the real, shipping
implementation so the contract is grounded in observed behaviour rather than
aspirational design. Where DataGrid narrows or extends the DataGrid baseline
(filtering UI, multi-select model, controlled-state ownership) the deviation is
called out explicitly below.

The implementation builds on `@tanstack/react-table` for the table engine; the
contract documents the public surface, not the engine internals.

---

## 2. Data model

DataGrid is generic over the row type `TData`. Columns are TanStack Table
`ColumnDef<TData>` records; the contract re-exports the type so callers do not
import the engine directly:

```typescript
// Re-exported from @tanstack/react-table for caller ergonomics.
// The contract treats these as opaque value types — the column engine's column
// definition shape is the source of truth.
export type {
  ColumnDef as DataGridColumnDef,
  SortingState,
  RowSelectionState,
} from '@tanstack/react-table'
```

```typescript
interface DataGridProps<TData> {
  // Data
  data: TData[]                                    // Full row set the table presents
  columns: ColumnDef<TData>[]                      // Ordered column definitions
  getRowId?: (row: TData, index: number) => string // Stable row identity (required for controlled selection)

  // Controlled sort (TanStack model — array of {id, desc})
  sorting?: SortingState
  onSortingChange?: OnChangeFn<SortingState>

  // Controlled row selection (TanStack model — { [rowId]: boolean })
  rowSelection?: RowSelectionState
  onRowSelectionChange?: OnChangeFn<RowSelectionState>

  // Bulk-actions slot — rendered in the toolbar when selection is non-empty
  bulkActions?: React.ReactNode

  // States
  isLoading?: boolean                              // Skeleton rows (~6) while truthy
  emptyState?: React.ReactNode                     // Custom empty-state node; renders default text when omitted
}
```

> The "minimal initial interface" rule from DataGrid §2 applies here: additions
> must preserve the defaults in §3 and the event semantics in §4. The TanStack
> column-engine types are the canonical column shape; we re-export them rather than
> mirror them so caller code remains compatible with the engine ecosystem.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `data` | `TData[]` | _required_ | The full row set the table presents. Empty array → empty state (§5). |
| `columns` | `ColumnDef<TData>[]` | _required_ | Ordered column definitions; render left-to-right in array order. |
| `getRowId` | `(row, index) => string` | TanStack default (row index) | Stable row identity. **Required** when `rowSelection` is supplied; without it, controlled selection cannot survive data re-orderings. |
| `sorting` | `SortingState` (`{id, desc}[]`) | uncontrolled | Controlled sort state. Single-sort baseline: at most one entry. |
| `onSortingChange` | `OnChangeFn<SortingState>` | — | Fires when the user toggles a sortable header. |
| `rowSelection` | `RowSelectionState` (`{[rowId]: boolean}`) | uncontrolled | Controlled selection state. Presence of both `rowSelection` and `onRowSelectionChange` switches the component into **selection-enabled mode** (renders the leading select column and the bulk-actions toolbar). |
| `onRowSelectionChange` | `OnChangeFn<RowSelectionState>` | — | Fires when the user toggles a row or the header checkbox. |
| `bulkActions` | `ReactNode` | — | Slot content rendered in the bulk-actions toolbar when selection is non-empty (§5). |
| `isLoading` | `boolean` | `false` | When `true`, the table body shows ~6 skeleton rows in place of data; all body interaction is suppressed. |
| `emptyState` | `ReactNode` | adapter default "No results." | Custom node rendered in place of the empty-state default when `data.length === 0` and not loading. |

### 3.1 Controlled-state model

DataGrid is **controlled by default for both sort and selection** — the host owns
the state and DataGrid reflects it. This deviates from DataGrid's
client-side-default baseline (DataGrid §4 "Controlled vs. uncontrolled"): the
React-track implementation treats data operations as host-owned because property-
management/ERP hosts almost always pair the table with server-side queries.

- Server-side sort/selection is the **default** integration model.
- Client-side sort over the supplied `data` is possible by **omitting**
  `sorting` / `onSortingChange` — the column engine then manages local state.
  Note: the implementation sets `manualSorting: true`, so the host is expected
  to re-supply sorted `data` when it owns sorting. Pure-client-side sort is a
  deferred enhancement (§7).

### 3.2 Selection-enabled mode

When **both** `rowSelection` and `onRowSelectionChange` are supplied
(`selectionEnabled` in the implementation), DataGrid:

1. Prepends a leading select-checkbox column (`id: '__select__'`, width 40px,
   `enableSorting: false`).
2. Renders a bulk-actions toolbar above the table body **when** the current
   selection is non-empty. The toolbar shows the selected count, the host's
   `bulkActions` content, and a "Clear" button that calls
   `table.resetRowSelection()`.

When either prop is omitted, both the leading column and the toolbar are absent.

### 3.3 Server-side data-flow contract (G-DGC1)

When the host owns sorting (controlled mode — `sorting` + `onSortingChange` both
supplied), the **expected host-side loop** is:

1. User clicks a sortable column header → DataGrid fires `onSortingChange(nextState)`.
2. Host updates its local `sorting` state.
3. Host re-issues the server query with the new sort parameters.
4. Host sets `isLoading={true}` while the query is in-flight.
5. Query resolves → host sets `data={results}` and `isLoading={false}`.

DataGrid's `manualSorting: true` configuration means the engine **never re-orders
`data` client-side**; it passes sort state changes to the host and renders whatever
`data` is supplied on the next render. A host that forgets to re-query after
`onSortingChange` will see the old data rendered with the new sort indicator — this
is a host integration error, not a DataGrid bug.

**Loading precedence:** `isLoading={true}` always wins over empty `data`. A
component mounted with `data={[]}` and `isLoading={true}` renders skeleton rows,
not the empty state. The transition is:
- `isLoading: true` → skeleton rows (regardless of `data`)
- `isLoading: false` + `data.length > 0` → data rows
- `isLoading: false` + `data.length === 0` → empty state (§5)

### 3.4 Imperative API (G-DGC3)

DataGrid does **not** forward a `ref` or expose an imperative handle in M1. There
is no `DataGridRef` type and no `useImperativeHandle` surface. Hosts that need to
trigger programmatic selection-clear, scroll-to-row, or focus management must do
so by updating the controlled props (`rowSelection`, `sorting`) rather than calling
methods on the component.

A `DataGridRef` imperative API (e.g., `ref.current.clearSelection()`,
`ref.current.scrollToRow(id)`) is a deferred feature (§7).

### 3.5 HTML attribute passthrough

The current implementation does **not** spread arbitrary HTML attributes on the
root element. The root is a `<div className="flex flex-col">` with no `id`,
`data-testid`, or ARIA passthrough.

This is a **known gap** relative to DataGrid §3.1 (which mandates a stable
anchor and host-attribute splat). The Accessibility contract (PAO) and a future
contract amendment will close this gap; the M1 contract documents the current
state honestly rather than over-promising.

---

## 4. Events — semantics

DataGrid does not emit a custom event surface; it forwards the TanStack column-
engine state changes via the controlled `onSortingChange` / `onRowSelectionChange`
callbacks.

| Callback | Payload | Fired when |
| --- | --- | --- |
| `onSortingChange` | `SortingState` (next state, or updater function) | The user activates a sortable column header. Single-sort baseline: the engine yields a `SortingState` of length 0 or 1. |
| `onRowSelectionChange` | `RowSelectionState` (next state, or updater function) | The user toggles a row checkbox or the header checkbox. Payload is the **full** next selection map (TanStack convention), not a delta. |

Both callbacks follow the TanStack `OnChangeFn<T>` convention: the value passed
may be either the next state directly, or an updater function `(prev) => next`.
Host code must handle both forms (typically `setState(updater)`).

**Row activation (`onRowClick`) is not in scope for M1.** DataGrid §4 specifies
`onRowClick`; the React implementation does not currently expose it. The visual
hover affordance on rows is present (the `hover:bg-gray-50` style) but no event
fires. This is a **deferred feature** for M1 (§7).

---

## 5. Slots

DataGrid uses React's `ReactNode` slot pattern (not the WC-track named-slot
pattern from DataGrid §5). The slots are:

| Slot prop | Purpose | Relationship to defaults |
| --- | --- | --- |
| `bulkActions` | Toolbar content shown when selection is non-empty. Hosts typically render a row of action buttons (Delete / Export / etc.). The toolbar DOM structure is: `<div>` containing a count label (`"{N} selected"`), the host's `bulkActions` node, and a "Clear" button. The host's node renders between count and Clear. | Slot is rendered **only** when `selectionEnabled` and the current selection is non-empty. Passing `bulkActions={null}` or `bulkActions={undefined}` hides the host's portion but the toolbar (with count + Clear) still appears when selection is non-empty. |
| `emptyState` | Custom empty-state content. | **Overrides** the default `<p>No results.</p>` placeholder when `data.length === 0` and `isLoading === false`. |
| Column `cell` and `header` (via `ColumnDef`) | Per-column custom content. | Engine-level: `ColumnDef.cell` / `ColumnDef.header` accept render functions; the M0 DataGrid `render` prop is realised here as `ColumnDef.cell`. |

There is **no `loading` slot** in M1: the loading state is the fixed
6-skeleton-row treatment. A `loadingSlot` prop is a deferred feature (§7).

---

## 6. Component composition

- **Selection column.** Internally injected as a synthetic `__select__` column
  when selection is enabled. Hosts do not declare it; they should not declare a
  column with `id: '__select__'` of their own.
- **Bulk-actions toolbar.** Internal to DataGrid; rendered above `<table>` when
  selection is non-empty. Hosts populate it via the `bulkActions` slot.
- **Pagination.** **Not bundled in M1.** DataGrid does not render a Pager. Hosts wire
  pagination via the sibling [Pager](./Pager.Semantic.md) component placed below
  the table, with the host computing `page` / `total` / `pageSize` and feeding
  paginated `data` to DataGrid. *Superseded for wave-3: see §FS-1.1 — the `pagination`
  prop adds a built-in pager footer.*
- **Filter UI.** **Not bundled in M1.** Hosts compose filtering via a ListToolbar above
  the table (mirroring DataGrid §6) and feed filtered `data` to DataGrid. *Superseded
  for wave-3: see §FS-1.2 — `columnFilters` adds an in-grid operator-menu filter model.*

---

## 7. Deferred features

The following are explicitly **out of scope for M1**; they were either deferred at
M0 (and remain deferred for the React track) or are tracked as M1 gaps:

- **Row activation event triplet (`onRowClick`, `onRowDoubleClick`, `onRowContextMenu`).** (G-DGC5)
  All three row-activation events specified in DataGrid §4 are absent in M1. The
  visual hover affordance on rows is present (`hover:bg-gray-50`); none of the
  three callbacks fire. The full triplet is deferred together so M2 can ship them
  as a unit with a consistent `DataGridRowEvent<TData>` payload type.
- **HTML attribute passthrough on root.** Stable `id`, `data-testid`, ARIA-linkage
  (DataGrid §3.1). Currently absent.
- **`loading` slot for custom indicators.** Currently fixed to the
  6-skeleton-row default.
- **Pure-client-side sort.** The engine is configured with `manualSorting: true`;
  flipping to engine-local sort when `sorting` is uncontrolled is a future
  enhancement.
- **Multi-column sort.** Single-sort baseline (per DataGrid §7).
- **Per-column filter UI**, **row drag-and-drop**, **column resize / reorder /
  freeze**, **virtualisation**, **grouping / aggregates**, **in-cell editing**,
  **export**. All deferred per DataGrid §7; the React track does not advance
  these in M1.

---

## 8. Open questions (for council)

1. **Selection-enabled trigger.** Today selection mode is implicit (presence of
   both controlled props). Should there be an explicit `selectable?: boolean` /
   `selectable?: 'none' | 'single' | 'multiple'` prop to mirror DataGrid §3?
   (Leaning: keep the implicit pattern for M1 to match the shipping API; add
   the explicit prop in a later wave alongside `'single'` mode.)
2. **Empty-state inside the table body.** The implementation renders the
   `emptyState` slot inside a `<td colSpan={effectiveColumns.length}>`. This
   makes the slot content a grid descendant (good for layout; potentially
   surprising for hosts who pass a full-bleed marketing-style empty state).
   Confirm this is the intended placement.
3. **`__select__` reserved column id.** Hosts could in principle declare a
   column with `id: '__select__'`. Should the contract reserve the id (the
   implementation silently overrides on selection mode), or rename the
   internal id to a less collision-prone token (e.g. `'@@select'`)?

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/datagrid/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## Full-surface expansion (2026-06-11 — waves 3-5, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/2026-06-11-datagrid-kendo-gap-analysis.md` — 26-area
matrix, waves 3-5. Every PARTIAL or MISSING row from that matrix is specced below, organised by
wave. Rows graded DONE, N/A, or excluded (AI assistant #23; row/column spanning #21; stacked/
adaptive layout #22) receive a single out-of-scope line in §FS-0.

**Supersedes:** the "deferred features" bullet list in §7 above — each item mentioned there that
falls in waves 3-5 is now specced below and the §7 bullet is superseded by the applicable §FS-*
section. The §7 list remains as a historical record of what was deferred from M1.

---

### §FS-0 Explicitly out of scope (this contract)

| Gap-matrix row | Reason |
|---|---|
| #7 Column visibility/chooser | Graded DONE ~90% in gap matrix; see Semantic §3 (controlled `columnVisibility`). |
| #12 Cell customisation/templates | Graded DONE; realised via `ColumnDef.cell` / `ColumnDef.header`. |
| #19 Context menu | Graded DONE ~80%; `rowActions` prop ships in current implementation. |
| #21 Row/column spanning | Not in the wave roadmap; no Kendo-parity requirement for MVP. |
| #22 Stacked/adaptive layout mode | Not in the wave roadmap; horizontal-scroll fallback is sufficient for MVP. |
| #23 AI assistant integration | Explicitly excluded from all waves per gap matrix. |
| #25 Virtualization | Declared wave-5 (spec only; no implementation gate before wave-5 acceptance). |

---

### §FS-1 Wave-3 — core table UX

#### §FS-1.1 Pagination integration (gap-matrix row #2)

Built-in pager wired directly into DataGrid's TanStack pagination row model.

```typescript
import type { PaginationState, OnChangeFn } from '@tanstack/react-table'

// Re-export for caller ergonomics (add to the existing re-export block).
export type { PaginationState }

// New props added to DataGridProps<TData>:
pagination?: PaginationState            // { pageIndex: number; pageSize: number }
onPaginationChange?: OnChangeFn<PaginationState>
pageSizeOptions?: number[]              // e.g. [10, 25, 50, 100]; default [10, 25, 50]
```

**Semantics:**

- When `pagination` is supplied together with `onPaginationChange`, DataGrid enables
  `getPaginationRowModel()` internally and wires the controlled state.
- `manualPagination: true` is the default (server-side): the host supplies a pre-paginated `data`
  slice and updates it on `onPaginationChange`. Client-side pagination (engine slices `data`
  locally) activates when `pagination` is supplied but `manualPagination` is omitted or
  `false` — explicit `manualPagination?: boolean` prop controls this.
- `pageSizeOptions` drives the page-size selector rendered in the built-in pager footer. Defaults
  to `[10, 25, 50]` when omitted.
- `rowCount?: number` — total row count for server-side page calculation (required for server-side
  mode; engine derives page-count from `rowCount / pageSize`).
- The built-in pager renders **below** the table inside the DataGrid root. Hosts who need an
  external pager (current M1 pattern) may continue to omit `pagination` entirely.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `pagination` | `PaginationState` | omitted (no pager rendered) | Controlled page state. Presence enables the built-in pager footer. |
| `onPaginationChange` | `OnChangeFn<PaginationState>` | — | Fires on page-index or page-size change. |
| `pageSizeOptions` | `number[]` | `[10, 25, 50]` | Page size choices in the pager's size selector. |
| `rowCount` | `number` | — | Total row count for server-side page calculation. |
| `manualPagination` | `boolean` | `true` when `pagination` is supplied | `false` = engine slices `data` locally; `true` = host supplies pre-sliced `data`. |

**wave-3**

---

#### §FS-1.2 Filtering operators + per-column operator menu (gap-matrix row #4)

Extended filter model with named operators and per-column operator-selection UI.

```typescript
export type FilterOperator =
  | 'contains'
  | 'eq'
  | 'neq'
  | 'gt'
  | 'lt'
  | 'gte'
  | 'lte'
  | 'startswith'
  | 'endswith'
  | 'isnull'
  | 'isnotnull'   // wave-4 — Kendo minimum; no value input when selected
  | 'isempty'     // wave-4 — Kendo minimum; string-family, no value input
  | 'isnotempty'  // wave-4 — Kendo minimum; string-family, no value input

export interface DataGridColumnFilter {
  id: string                  // column id
  operator: FilterOperator    // selected operator
  value: string               // filter value (empty string is valid for the no-value operators)
}

export type DataGridColumnFiltersState = DataGridColumnFilter[]

// New props:
columnFilters?: DataGridColumnFiltersState
onColumnFiltersChange?: (filters: DataGridColumnFiltersState) => void
```

**Semantics:**

- `columnFilters` + `onColumnFiltersChange` replace the existing internal `ColumnFiltersState`
  managed by `showFilterRow`. When the controlled pair is supplied, DataGrid uses the host's
  filter state; the existing `showFilterRow` prop is superseded by the controlled pair (hosts may
  continue to use `showFilterRow` for simple uncontrolled client-side contains-filtering).
- `manualFiltering: boolean` (default `true`): when `true` the engine does not apply filters
  locally — the host re-queries with the new filter state. When `false` the engine applies filters
  client-side using the `operator` field to select the TanStack filter function.
- Each column's header-row filter input shows an operator-selector button (funnel icon) that opens
  an operator-menu. The menu items are the allowed operators for the column's data type; default
  allowed set is all `FilterOperator` values.
- `allowedOperators?: Record<string, FilterOperator[]>` — per-column override of the allowed
  operator set. Key is `columnId`.
- The column menu (§FS-1.6) exposes a "Filter" section when `columnFilters` is supplied.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `columnFilters` | `DataGridColumnFiltersState` | omitted | Controlled filter state per column with operator. Presence enables the extended filter UI. |
| `onColumnFiltersChange` | `(filters: DataGridColumnFiltersState) => void` | — | Fires when any column filter changes (operator or value). |
| `manualFiltering` | `boolean` | `true` when `columnFilters` supplied | `false` = client-side operator application; `true` = host handles filtering. |
| `allowedOperators` | `Record<string, FilterOperator[]>` | all operators | Per-column override of the operator-menu items. |

**wave-3**

---

#### §FS-1.3 Selection modes (gap-matrix row #8)

Explicit `selectionMode` prop replaces the implicit "both-props-present" trigger.

```typescript
export type DataGridSelectionMode = 'none' | 'single' | 'multiple'

// New prop (added to DataGridProps<TData>):
selectionMode?: DataGridSelectionMode   // default inferred (see below)
```

**Semantics:**

- `'none'` — no selection column, no bulk-actions toolbar, no row-click selection. Equivalent to
  the M1 state when neither `rowSelection` nor `onRowSelectionChange` is supplied.
- `'single'` — clicking a row (or its checkbox) sets exactly one entry in `RowSelectionState`; any
  prior selection is cleared. No header tri-state checkbox is rendered. Shift+click and checkbox
  column behave identically (select one, deselect others).
- `'multiple'` — the M1 default multiple-checkbox behaviour. Header tri-state checkbox present.
- **Migration:** when `selectionMode` is omitted, DataGrid infers: `'multiple'` if both
  `rowSelection` and `onRowSelectionChange` are supplied; `'none'` otherwise. This preserves M1
  backward compatibility.
- `onRowClick?: (row: TData, event: React.MouseEvent) => void` — fires on a non-checkbox row
  click when `selectionMode` is `'single'` or `'multiple'`. This closes the `onRowClick` gap from
  Semantic §4 / §7 (G-DGC5 partial closure; `onRowDoubleClick` and `onRowContextMenu` remain
  deferred to a later wave).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `selectionMode` | `'none' \| 'single' \| 'multiple'` | inferred from prop presence | Explicit selection model. |
| `onRowClick` | `(row: TData, event: React.MouseEvent) => void` | — | Fires on non-checkbox row activation; available in `'single'` and `'multiple'` modes. |

**wave-3**

---

#### §FS-1.4 Column reorder (gap-matrix row #6)

Drag-header column reorder using TanStack's built-in `columnOrder` feature model.

```typescript
import type { ColumnOrderState } from '@tanstack/react-table'
export type { ColumnOrderState as DataGridColumnOrderState }

// New props:
columnOrder?: ColumnOrderState          // string[] of column ids in display order
onColumnOrderChange?: OnChangeFn<ColumnOrderState>
```

**Semantics:**

- When `columnOrder` + `onColumnOrderChange` are supplied, DataGrid passes the state to TanStack's
  `columnOrder` model and enables drag-reorder on column headers.
- The drag affordance is a drag handle (or full-header drag region) on non-pinned, non-`__select__`
  columns. Pinned columns (`pinnedColumns`) and the `__select__` column are not reorderable.
- `onColumnOrderChange` fires with the new `ColumnOrderState` after a successful drop. DataGrid
  does NOT reorder `columns` internally — it passes the ordered ids to TanStack; the engine renders
  columns in `columnOrder` order.
- When `columnOrder` is omitted, drag reorder is disabled (headers are not draggable).
- Column reorder is uncontrolled when `columnOrder` is supplied but `onColumnOrderChange` is
  omitted: the engine manages `columnOrder` locally (useful for purely visual reordering in
  client-only mode).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `columnOrder` | `ColumnOrderState` | omitted (no reorder) | Controlled column display order. |
| `onColumnOrderChange` | `OnChangeFn<ColumnOrderState>` | — | Fires after a drag-drop column reorder completes. |

**wave-3**

---

#### §FS-1.5 Editing modes: row-mode, dialog-mode, batch, and validation (gap-matrix row #9)

Extended editing surface beyond the M1 in-cell (`editMode: 'cell'`) baseline.

```typescript
export type DataGridEditMode = 'cell' | 'row' | 'dialog'

// Validation hook — return a string (error message) or null (valid).
export type DataGridCellValidator<TData> = (
  row: TData,
  columnId: string,
  value: string,
) => string | null

// Batch-edit state — map of { [rowId]: { [columnId]: pendingValue } }
export type DataGridPendingChanges = Record<string, Record<string, string>>

// New/updated props:
editMode?: DataGridEditMode             // default 'cell' (preserves M1 behaviour)
validate?: DataGridCellValidator<TData> // optional per-cell validation
// Batch edit (editMode='cell' or 'row' with deferred commit):
pendingChanges?: DataGridPendingChanges
onPendingChangesChange?: (changes: DataGridPendingChanges) => void
onCommitChanges?: (changes: DataGridPendingChanges) => void
onDiscardChanges?: () => void
```

**Semantics:**

- `editMode: 'cell'` — M1 behaviour: double-click or Enter activates in-cell editor; commit on
  Enter/blur; `onCellEdit` fires per commit. The `validate` hook fires on every commit attempt;
  a non-null return blocks the commit and renders the error message below (or inside) the cell.
- `editMode: 'row'` — clicking a row's "Edit" action (or an `editRow(rowId)` trigger from
  `onRowClick`) switches that row into row-edit mode: all editable cells in the row become inputs
  simultaneously. A per-row "Save" button commits all cells in the row via `onCellEdit` (once per
  changed cell). A "Cancel" button reverts. `validate` fires per cell on Save; all errors must
  clear before the row commits.
- `editMode: 'dialog'` — clicking an "Edit" action opens a `Dialog` component pre-populated with
  the row's editable fields. Commit fires `onCellEdit` for each changed field. `validate` fires per
  field on dialog submit. The dialog close (X or Cancel) discards. The DataGrid contract owns the
  trigger and field wiring; the Dialog itself is the host's to compose (see `renderEditDialog`
  below).
- `renderEditDialog?: (row: TData, onSave: (changes: Record<string, string>) => void, onClose: () => void) => ReactNode` — required when `editMode === 'dialog'`. DataGrid invokes it to render the dialog content.
- **Batch mode** (opt-in on top of `'cell'` or `'row'` mode): when `pendingChanges` +
  `onPendingChangesChange` are both supplied, DataGrid accumulates edits into `pendingChanges`
  instead of firing `onCellEdit` immediately. A "Commit" / "Discard" bar appears above the grid
  (similar to the bulk-actions bar) when `pendingChanges` is non-empty. `onCommitChanges` fires
  with the full pending map. `onDiscardChanges` clears all pending changes.
- `pendingChanges` cells render a "dirty" visual indicator (left-accent border on the cell).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `editMode` | `'cell' \| 'row' \| 'dialog'` | `'cell'` | Edit interaction model. |
| `validate` | `(row, columnId, value) => string \| null` | — | Per-cell validator; non-null blocks commit and surfaces error. |
| `pendingChanges` | `DataGridPendingChanges` | — | Controlled batch-pending state. |
| `onPendingChangesChange` | `(changes: DataGridPendingChanges) => void` | — | Fires on each cell edit when batch mode is active. |
| `onCommitChanges` | `(changes: DataGridPendingChanges) => void` | — | Fires when the user confirms the batch commit. |
| `onDiscardChanges` | `() => void` | — | Fires when the user discards all pending changes. |
| `renderEditDialog` | `(row, onSave, onClose) => ReactNode` | — | Required for `editMode === 'dialog'`; renders the dialog content. |

**wave-3**

---

#### §FS-1.6 Column menu expansion (gap-matrix row #20)

Extends the existing `showColumnMenu` / `ColumnMenu` surface to include filter and chooser.

No new top-level props required — the column menu items expand automatically when the relevant
features are active:

- When `columnFilters` + `onColumnFiltersChange` are supplied (§FS-1.2), the column menu includes
  a "Filter…" item that opens the operator-menu inline for that column.
- When `showColumnChooser?: boolean` is `true`, the column menu includes a "Columns…" item that
  opens a column-chooser flyout listing all columns with toggle checkboxes. This closes the ~40%
  gap in gap-matrix row #7 for the chooser-list sub-feature.

```typescript
// New prop:
showColumnChooser?: boolean   // default false; adds "Columns…" to every column menu
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `showColumnChooser` | `boolean` | `false` | Adds a column-visibility chooser panel accessible from each column menu. |

**wave-3**

---

#### §FS-1.7 Footer cells (gap-matrix row #14)

Per-column footer renderer.

```typescript
// No new top-level DataGrid prop — footer is declared per column via ColumnDef:
//   ColumnDef<TData>.footer?: ColumnDefTemplate<HeaderContext<TData, unknown>>
// (This is the standard TanStack footer field; DataGrid renders a <tfoot> row when
// at least one column declares a non-null footer.)
```

**Semantics:**

- DataGrid renders a `<tfoot>` row when `columns.some(c => c.footer != null)`.
- Each `<td>` in the footer row calls `flexRender(column.columnDef.footer, header.getContext())`.
- Columns with no `footer` field render an empty `<td>`.
- Footer cells are **not** editable or sortable.
- The footer row is rendered **below** the table body and **above** the built-in pager (when
  present). When `pinnedColumns` is active, pinned footer cells are sticky (matching header sticky
  behaviour).

**No new DataGrid-level prop is required.** The `ColumnDef.footer` field (standard TanStack) is the
declaration surface; this section documents that DataGrid honours it and renders `<tfoot>`.

**wave-3**

---

#### §FS-1.8 Multi-sort modifier-click UX (gap-matrix row #3)

Closes the ~20% gap in the MOSTLY-graded sorting row.

```typescript
// New prop:
enableMultiSort?: boolean     // default false (preserves single-sort M1 baseline)
multiSortKey?: 'shift' | 'meta' | 'ctrl'   // default 'shift'
```

**Semantics:**

- When `enableMultiSort === true`, Shift+click (or the configured `multiSortKey`+click) on a
  header appends that column to the active `SortingState` rather than replacing it. Plain click
  still replaces (single-sort behaviour).
- `onSortingChange` fires with the multi-entry `SortingState`; the host receives and re-queries.
- The sort chip bar (`showSortChips`) lists all active sort entries; each chip's × button removes
  only that column's sort (existing behaviour already handles this).
- When `enableMultiSort === false` (default), multi-sort is disabled regardless of modifier key.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `enableMultiSort` | `boolean` | `false` | Enables Shift+click multi-column sort. |
| `multiSortKey` | `'shift' \| 'meta' \| 'ctrl'` | `'shift'` | Modifier key that triggers multi-sort append. |

**wave-3**

---

### §FS-2 Wave-4 — structure

#### §FS-2.1 Grouping + group expansion + aggregates (gap-matrix rows #5, #15)

TanStack built-in `getGroupedRowModel` + aggregation model.

```typescript
import type { GroupingState, AggregationFn } from '@tanstack/react-table'
export type { GroupingState as DataGridGroupingState }

export type DataGridAggregateFunction = 'sum' | 'avg' | 'min' | 'max' | 'count'

export interface DataGridColumnAggregate {
  fn: DataGridAggregateFunction
  // Optional custom label: defaults to the function name.
  label?: string
}

// New props:
grouping?: GroupingState                // string[] of column ids to group by
onGroupingChange?: OnChangeFn<GroupingState>
aggregates?: Record<string, DataGridColumnAggregate>  // columnId → aggregate spec
renderGroupRow?: (groupValue: unknown, row: import('@tanstack/react-table').Row<TData>, depth: number) => React.ReactNode
```

**Semantics:**

- When `grouping` + `onGroupingChange` are supplied, DataGrid enables
  `getGroupedRowModel()` + `getExpandedRowModel()` internally. Rows are grouped
  by the listed column ids in order (first id = outermost group).
- Group rows render a dedicated `<tr>` with a chevron expand/collapse control and the group-key
  value. `renderGroupRow` replaces the default rendering when supplied.
- `aggregates` specifies per-column aggregate functions rendered in the group-row footer cell for
  that column. The engine computes aggregates using TanStack's built-in `aggregationFns`; the
  mapping is `'sum' → sum`, `'avg' → mean`, `'min' → min`, `'max' → max`, `'count' → count`.
- Expanding/collapsing a group uses TanStack's `toggleExpanded()`. Expanded state is managed
  internally (uncontrolled); no external `expandedGroups` prop in wave-4 (deferred to a later
  patch if hosts need server-side group expansion).
- The aggregates status bar (gap-matrix row #15) is realised as the footer cells of group rows
  rather than a separate detached bar. A detached "status bar" surface is deferred past wave-4.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `grouping` | `GroupingState` | omitted (no grouping) | Controlled grouping column order. |
| `onGroupingChange` | `OnChangeFn<GroupingState>` | — | Fires when the user reorders or clears grouping. |
| `aggregates` | `Record<string, DataGridColumnAggregate>` | — | Per-column aggregate function for group-row subtotals. |
| `renderGroupRow` | `(groupValue, row, depth) => ReactNode` | default chevron + group-key label | Custom group-row renderer. |

**wave-4**

---

#### §FS-2.2 Detail rows / row expansion (gap-matrix row #10)

```typescript
import type { ExpandedState } from '@tanstack/react-table'
export type { ExpandedState as DataGridExpandedState }

// New props:
renderDetail?: (row: TData) => React.ReactNode
expanded?: ExpandedState               // controlled; { [rowId]: boolean } or true (all)
onExpandedChange?: OnChangeFn<ExpandedState>
```

**Semantics:**

- When `renderDetail` is supplied, DataGrid prepends a synthetic `__expand__` column (width 36px)
  containing a chevron toggle button for each row.
- Clicking the chevron (or pressing Enter/Space on it) toggles that row's expanded state.
- When a row is expanded, DataGrid inserts a `<tr class="detail-row">` immediately below the
  data row, spanning all columns, containing the result of `renderDetail(row.original)`.
- `expanded` + `onExpandedChange` are the controlled expanded-state pair (TanStack `ExpandedState`
  model). When omitted, DataGrid manages expansion state internally.
- `renderDetail` and `grouping` (§FS-2.1) are mutually exclusive in wave-4; the combination is
  deferred.
- The `__expand__` column is not reorderable or pinnable.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `renderDetail` | `(row: TData) => ReactNode` | omitted (no detail rows) | Detail-row content renderer. Presence enables the expand column. |
| `expanded` | `ExpandedState` | uncontrolled | Controlled expand state. |
| `onExpandedChange` | `OnChangeFn<ExpandedState>` | — | Fires on row expand/collapse. |

**wave-4**

---

#### §FS-2.3 Row pinning (gap-matrix row #11)

```typescript
import type { RowPinningState } from '@tanstack/react-table'
export type { RowPinningState as DataGridRowPinningState }

// New props:
rowPinning?: RowPinningState            // { top?: string[]; bottom?: string[] } of row ids
onRowPinningChange?: OnChangeFn<RowPinningState>
```

**Semantics:**

- Uses TanStack's built-in `getTopRows()` / `getBottomRows()` / `getCenterRows()` models.
- Pinned-top rows render above the normal body rows; pinned-bottom rows render below.
- Pinned rows have an elevated visual treatment (box-shadow or background — see Styling §FS).
- Pinned rows participate in selection and editing normally.
- When `rowPinning` is omitted, row pinning is disabled.
- Programmatic pinning only in wave-4 (drag-to-pin UX deferred).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `rowPinning` | `RowPinningState` | omitted (no pinning) | Controlled pinned-row sets (top / bottom). |
| `onRowPinningChange` | `OnChangeFn<RowPinningState>` | — | Fires when the pin set changes. |

**wave-4**

---

#### §FS-2.4 Stacked / multi-row headers (gap-matrix row #13)

Closes the ~40% gap in the PARTIAL header row.

TanStack's header-group model already supports stacked headers through nested `ColumnDef.columns`.
DataGrid renders all `table.getHeaderGroups()` rows today (the implementation iterates
`headerGroups.map(...)`) — the gap is **documentation**, not a missing feature.

**Contract clarification:**

- DataGrid renders all TanStack header groups produced by nested `ColumnDef.columns`. A host who
  wants stacked headers declares nested columns:
  ```typescript
  { header: 'Address', columns: [{ accessorKey: 'street' }, { accessorKey: 'city' }] }
  ```
- Each group row is a `<tr>` inside `<thead>`. The outer group cell spans the child columns via
  the `colSpan` TanStack provides on each header.
- Sticky-header elevation (`sticky top-0 z-10`) applies to the `<thead>` element; all header group
  rows are covered.
- No new DataGrid-level prop is required. This section documents that the existing rendering loop
  already supports stacked headers.

**wave-4**

---

### §FS-3 Wave-5 — platform

#### §FS-3.1 Toolbar slot (gap-matrix row #18)

```typescript
// New prop:
toolbar?: React.ReactNode
```

**Semantics:**

- When `toolbar` is supplied, DataGrid renders a toolbar band **above** the bulk-actions bar (and
  above the sort-chip bar when present). The host composes the toolbar content: search inputs,
  filter dropdowns, export buttons, view-toggle controls.
- The toolbar band has no default content; it is a plain slot (`<div role="toolbar"
  aria-label="Grid toolbar">`).
- When neither `toolbar` nor `bulkActions` content (selection active) is present, no toolbar band
  is rendered.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `toolbar` | `ReactNode` | omitted (no toolbar band) | Host-composed toolbar rendered above the grid. |

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.2 Grid-integrated search (gap-matrix row #17)

```typescript
// New props:
globalFilter?: string
onGlobalFilterChange?: (value: string) => void
```

**Semantics:**

- When `globalFilter` + `onGlobalFilterChange` are supplied, DataGrid enables TanStack's
  `getFilteredRowModel()` with the global filter value.
- `manualFiltering: true` (from §FS-1.2) also governs global filter — when `true`, the host
  handles the filter and DataGrid merely reports the value.
- The toolbar slot (§FS-3.1) is the recommended composition point for a `SearchInput` that drives
  `onGlobalFilterChange`. DataGrid does NOT render a built-in search input — the host composes it.
- `getExportRows(): TData[]` — imperative escape hatch (see §FS-3.3 below).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `globalFilter` | `string` | omitted | Controlled global search string. |
| `onGlobalFilterChange` | `(value: string) => void` | — | Fires when the global filter changes (debounce is the host's responsibility). |

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.3 Export hooks (gap-matrix row #16)

DataGrid does **not** own CSV / PDF / Excel generation — that is `DataExportButton`'s responsibility.
The integration surface is an escape hatch to access the grid's current filtered / sorted / paged
row set:

```typescript
// Added to DataGridRef (imperative handle — forward ref):
export interface DataGridRef<TData = unknown> {
  /** Returns the rows currently visible in the grid (after filtering, sorting, pagination). */
  getExportRows(): TData[]
}
```

**Semantics:**

- `DataGrid` forwards a `ref` prop typed as `React.Ref<DataGridRef<TData>>` in wave-5. The
  imperative handle exposes `getExportRows()` which calls
  `table.getFilteredRowModel().rows.map(r => r.original)` (or the pagination-bounded equivalent).
- The host passes `ref.current.getExportRows()` to `DataExportButton` (or any export utility) to
  compose CSV/PDF from the current grid view.
- No other imperative methods are added in wave-5; the `DataGridRef` shape from Semantic §3.4 is
  the foundation to extend.

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.4 Virtualization (gap-matrix row #25)

```typescript
// New props (wave-5 spec only; no implementation gate until wave-5 acceptance):
virtual?: boolean       // default false; enables row virtualization via @tanstack/react-virtual
rowHeight?: number      // default undefined (auto-estimated); explicit row height improves virtual performance
```

**Semantics:**

- When `virtual === true`, DataGrid replaces the standard `table.getRowModel().rows.map(...)` body
  render with a virtualised render using `@tanstack/react-virtual` (`useVirtualizer`).
- `rowHeight` is the estimated row height passed to `useVirtualizer`'s `estimateSize`. When
  omitted, the virtualiser estimates from the first measured row.
- Virtualization is incompatible with `renderDetail` (§FS-2.2) in wave-5 — expanded detail rows
  have variable height that breaks fixed-estimate virtualisation. The combination is unsupported
  and logs a warning.
- The `overflow-x-auto` scroll wrapper becomes `overflow-auto` (both axes scrollable) when
  `virtual === true`.
- `aria-rowindex` / `aria-colindex` on virtualised rows — see Accessibility §FS-3.4.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `virtual` | `boolean` | `false` | Enables row virtualisation. |
| `rowHeight` | `number` | estimated | Explicit row height hint for the virtualiser. |

**wave-5 (spec only in this file; implementation gated on wave-5 acceptance)**

---

#### §FS-3.5 Full keyboard navigation (gap-matrix row #24)

The existing arrow-key model (wave-1/2) already moves cell focus. Wave-5 extends the matrix and
couples it to the `role="grid"` council question (G-DGA1).

```typescript
// No new DataGrid props — keyboard behaviour is governed by the Accessibility contract §FS-3.
// Reference: Accessibility §FS-3.5 for the full keybinding table.
```

**Council gate G-DGA1:** The decision whether to adopt `role="grid"` + two-dimensional WAI-ARIA
keyboard model vs. remain on `role="table"` with enhanced focus management is owned by the
accessibility council. This section documents that the wave-5 keyboard expansion is **contingent
on that council verdict**. Both paths forward are specced in the Accessibility contract
(§FS-3.5 interim table path; §FS-3.5 grid-role target path).

---

### §FS-4 Wave-6 — column locking and server-side surface

#### §FS-4.1 Column locking (CG-8 row #2)

Per-column declarative locking via `DataGridColumnMeta.locked`. Columns marked `locked: true` are
pinned to the left and stay visible when the grid is horizontally scrolled, mirroring the Kendo
`GridColumn locked` prop and the pattern used in `Spreadsheet.frozenColumns`.

```typescript
// Extension to DataGridColumnMeta (existing interface):
export interface DataGridColumnMeta {
  editor?: 'text' | 'select'
  options?: string[]
  /** When true, the column is frozen/locked to the left side of the grid.
   *  Equivalent to Kendo GridColumn locked prop.
   *  Coexists with the host-level `pinnedColumns` prop — both contribute to the
   *  effective set of sticky-left columns. */
  locked?: boolean
}
```

**Semantics:**

- A column is "effectively pinned" when either: (a) its `id` appears in the `pinnedColumns` prop,
  OR (b) its `meta.locked === true`.
- Locked columns are always included in the effective pinned set regardless of `pinnedColumns`.
  Hosts that only use `meta.locked` may omit `pinnedColumns` entirely.
- The left-offset calculation applies to all effectively-pinned columns in their rendered order
  (left-to-right). Locked columns are ordered by their position in the `columns` array;
  `pinnedColumns` entries that are NOT in the columns array are ignored (same as today).
- The `locked` indicator is surfaced as a visual lock icon in the column header alongside the
  column label (aria-hidden), so users can identify frozen columns without column reordering.
- Column locking is **left-only** in wave-6. Right-side locking is out of scope and deferred.
- The `__select__` and `__expand__` synthetic columns are unaffected by `meta.locked`.

| ColumnMeta field | Type | Default | Meaning |
|---|---|---|---|
| `locked` | `boolean` | `false` | Freeze this column to the left; rendered with sticky positioning. |

**wave-6**

---

#### §FS-4.2 Server-side data API — documented conclusion (CG-8 row #1)

The council B3 verdict (filed with the dispatchable-gaps audit) explicitly rejected inventing a
consolidated `onDataStateChange(state: { sort, page, filters })` callback. The rationale: the
separate controlled props already supply the server-side loop the host needs, and a unified
callback creates a new surface that duplicates the existing props without adding expressiveness.

**Canonical server-side surface (already shipped in waves 3-5):**

| Callback | Controls |
|---|---|
| `onSortingChange` | Sort state (from TanStack `SortingState`) |
| `onPaginationChange` | Page index + page size (from TanStack `PaginationState`) |
| `onColumnFiltersChange` | Per-column filter operators + values |

**Host pattern:** the host passes all three callbacks; each fetches or updates a single dimension of
the server query and composes them as independent signals. This IS the server-side data API for
wave-6 — no unified event is added.

**wave-6 (documented conclusion — no new implementation)**

---

#### §FS-4.3 Export surface — documented conclusion (CG-8 row #3)

`DataGrid` does not own CSV / PDF / Excel generation. The full specced export surface is:

1. `DataGridRef.getExportRows()` — already shipped in wave-3 (§FS-3.3). Returns the filtered /
   sorted / paginated row set. The host passes this to `DataExportButton.onExport` or any custom
   export utility.
2. `DataExportButton` — already ships as a standalone button component (`buttons/DataExportButton`)
   with `onExport(format: ExportFormat)` callback and format dropdown.

There is no CSV serialization helper in DataGrid — that is host territory (or a separate utility
package decision). `ExportFormat` (`'csv' | 'xlsx' | 'pdf' | 'json'`) is already defined in
`DataExportButton`. The composition pattern is sufficient for all wave-6 export use cases.

**wave-6 (documented conclusion — no new implementation)**

---

#### §FS-4.4 Virtualization — deferred (CG-8 row #4)

The virtualization spec (§FS-3.4) was promoted to wave-5 spec-only. The implementation gate was
set to wave-5 acceptance. Wave-5 has shipped. The CG-8 gap audit rates this as effort L (8-16h)
and it is a standalone optimization (no blocking dependency). It is dispatchable as a standalone
wave-6+ cohort item but is **out of scope for this PR** — the column-locking implementation is
the CG-8 primary deliverable.

**wave-6+ (deferred to standalone cohort)**

**wave-5**
