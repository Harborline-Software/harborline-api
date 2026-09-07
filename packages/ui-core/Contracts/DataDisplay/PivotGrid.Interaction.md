# PivotGrid — Interaction Contract

- **Component:** PivotGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PivotGrid.Semantic.md) · [Interaction](./PivotGrid.Interaction.md) · [Accessibility](./PivotGrid.Accessibility.md) · [Styling](./PivotGrid.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/PivotGrid.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

PivotGrid is **read-only and non-interactive**. It renders a `<table>` with computed aggregate values. There is no click, selection, sort, or filter interaction in the current implementation.

---

## 2. No interaction surface

PivotGrid does **not**:

- Fire `onClick`, `onCellClick`, or any callback.
- Support row or cell selection.
- Support sorting by clicking column headers.
- Support drill-down on cell click.
- Participate in keyboard-driven selection.

---

## 3. Scroll behaviour

The PivotGrid is wrapped in `overflow-auto`. When the table exceeds its container's width (many columns), horizontal scroll is enabled by the browser. The scroll container has no custom scroll handling.

---

## 4. Data update behaviour

When `data`, `columns`, `rows`, or `measures` props change, PivotGrid re-renders synchronously with the new aggregated values. No animation or loading state is applied.

---

## 5. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No cell click / drill-down event | Hosts needing drill-down must replace PivotGrid with a custom solution |
| I2 | No column header sort | Deferred feature |
| I3 | No export from within the component | Host uses ExportCsvButton externally |

---

## Full-surface expansion (2026-06-11 — waves 2-4, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — multi-dimension rows and columns

**Status:** Draft

#### §FS-1.1 Multi-dimension data model

PivotGrid currently uses only `rows[0]`, `columns[0]`, `measures[0]`. Wave-2 lifts
that constraint:

```typescript
interface PivotGridAxis {
  field: string
  title?: string
  // Hierarchical: child members are distinct values of this field, grouped under a parent-axis member
}

// PivotGridDataField unchanged (aggregate function + format live on measures)

interface PivotGridProps {
  data: Array<Record<string, unknown>>
  rows: PivotGridAxis[]          // multi-dim: each entry adds a nesting level
  columns: PivotGridAxis[]       // multi-dim: each entry adds a nesting level
  measures: PivotGridDataField[] // multi-measure: each renders a sub-column per column member
  showGrandTotal?: boolean
  showSubtotals?: boolean        // NEW wave-2
  className?: string
  // Wave-2 additions:
  onCellClick?: (rowKeys: string[], colKeys: string[], measureField: string, value: number) => void
  cellRender?: (props: PivotCellRenderProps) => React.ReactNode
  headerCellRender?: (props: PivotHeaderCellRenderProps) => React.ReactNode
}

interface PivotCellRenderProps {
  rowKeys: string[]       // member values for each row axis, outermost first
  colKeys: string[]       // member values for each column axis, outermost first
  measureField: string
  value: number
  formattedValue: string
}

interface PivotHeaderCellRenderProps {
  axis: 'row' | 'column'
  field: string
  member: string
  level: number
  span: number
}
```

**Layout contract:** with M row dimensions and N column dimensions and K measures:
- The row header area has M header columns (one per row axis level).
- The column header area has (N + 1) rows: N rows for column axis labels + 1 row for
  measure labels when K > 1.
- Each column axis member spans K sub-columns (one per measure).

**wave-2**

---

#### §FS-1.2 Subtotals per dimension level

When `showSubtotals === true`, each group of members under a row-axis parent renders
a subtotal row immediately after its last member. The subtotal row applies the same
aggregate function (`measures[i].aggregate`) over the group slice.

Subtotal rows are visually distinct (bold, slightly elevated background token).

Grand-total row / column behavior is unchanged from M1.

Subtotals are not shown for the innermost (leaf) dimension level — a leaf has no
children to sub-total.

**wave-2**

---

### §FS-2 Wave-3 — expand/collapse members

**Status:** Draft

#### §FS-2.1 Expand/collapse state model

```typescript
// Addition to PivotGridProps:
expandedMembers?: Record<string, boolean>
  // key: "{axis}:{level}:{memberValue}" e.g. "row:0:PropertyA"
  // value: true = expanded, false/absent = collapsed
defaultExpandedMembers?: Record<string, boolean>
onExpandedMembersChange?: (next: Record<string, boolean>) => void
```

Leaf members (innermost row dimension) do not have an expand/collapse toggle.
All non-leaf members are expanded by default when `defaultExpandedMembers` is absent.

#### §FS-2.2 Toggle behavior

Clicking the chevron icon on a non-leaf row-axis member:
- Collapses the member: hides all child rows (including sub-totals for that group)
  and any further nested members.
- Updates `expandedMembers[key]` to `false` and fires `onExpandedMembersChange`.
- The cell in the collapsed row spans the full column area (its colspan covers all
  descendant row-axis columns that are now hidden).

Expanding reverses the above. The component re-renders the previously-hidden rows.

Keyboard: Enter / Space on the chevron cell toggles expand/collapse.
The toggle button carries `aria-expanded` (true / false) and `aria-controls` pointing
to the id of the row group.

**wave-3**

---

### §FS-3 Wave-3 — configurator panel

**Status:** Draft

#### §FS-3.1 PivotGridConfigurator component

A companion `PivotGridConfigurator` component renders a field-chooser panel.
It is a separate component, not embedded in `PivotGrid` itself (host composes them):

```typescript
interface PivotGridConfiguratorProps {
  fields: PivotGridAvailableField[]    // all data fields available to pivot
  rows: PivotGridAxis[]
  columns: PivotGridAxis[]
  measures: PivotGridDataField[]
  onRowsChange: (rows: PivotGridAxis[]) => void
  onColumnsChange: (columns: PivotGridAxis[]) => void
  onMeasuresChange: (measures: PivotGridDataField[]) => void
  // Optional filter + sort controls (wave-4):
  // filterFields?: ...; onFilterChange?: ...;
  // sortFields?: ...; onSortChange?: ...;
}

interface PivotGridAvailableField {
  field: string
  title?: string
  type: 'dimension' | 'measure'
  aggregate?: PivotGridDataField['aggregate']   // default for this field when dragged to measures
}
```

#### §FS-3.2 Configurator panel anatomy

The configurator renders four drop zones: Rows, Columns, Measures, Filters (wave-4).
Available fields in the left panel are dragged into the drop zones.

- Dragging a field into **Rows** appends it to `rows` and fires `onRowsChange`.
- Dragging a field into **Columns** appends it to `columns` and fires `onColumnsChange`.
- Dragging a field into **Measures** appends it with the field's default aggregate and
  fires `onMeasuresChange`.
- Dragging a field already in a zone to a different position within the zone reorders it.
- Dragging a field out of a zone (back to the available-fields list or to the trash icon)
  removes it from that zone and fires the relevant change callback.

#### §FS-3.3 Configurator keyboard support

Each field pill in a drop zone has a "Remove" button (×). Tab navigates between pills;
Delete / Backspace on a focused pill removes it (same as ×).
The drag-and-drop reordering is pointer-only in wave-3; keyboard reorder is deferred.

**wave-3**

---

### §FS-4 Wave-4 — cell click and custom cell rendering

**Status:** Draft

#### §FS-4.1 onCellClick

When `onCellClick` is supplied, data cells become interactive:
- Clicking a cell fires `onCellClick(rowKeys, colKeys, measureField, value)`.
- Cells receive `cursor-pointer` and `hover:bg-surface-hover` treatment when
  `onCellClick` is present.
- Keyboard: Tab navigates between clickable cells (within a row); Enter/Space activates.

Header cells do NOT fire `onCellClick`.

**wave-4**

---

#### §FS-4.2 cellRender and headerCellRender

`cellRender` replaces the default `<td>` content for data cells.
`headerCellRender` replaces the default content for column/row header cells.
Both receive typed props (see §FS-1.1 for prop shapes) and are responsible for
rendering the full cell content including the formatted value.

When `cellRender` is supplied, `onCellClick` still fires on the `<td>` wrapper
(the host's returned element is wrapped in the clickable `<td>`).

**wave-4**
