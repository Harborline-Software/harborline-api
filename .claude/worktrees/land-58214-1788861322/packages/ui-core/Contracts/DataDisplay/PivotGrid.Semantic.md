# PivotGrid — Semantic Contract

- **Component:** PivotGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PivotGrid.Interaction.md) · [Accessibility](./PivotGrid.Accessibility.md) · [Styling](./PivotGrid.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/PivotGrid.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled pivot table

---

## 1. Purpose

PivotGrid renders a cross-tabulated summary table: rows represent one dimension (e.g., property), columns represent another (e.g., month), and cell values are aggregated measures (e.g., sum of rent collected). An optional grand-total row and column are appended.

PivotGrid is a **read-only display component** — it renders aggregated data but provides no sorting, filtering, or editing UI. The reference implementation supports a single row dimension, a single column dimension, and a single measure. Multi-dimensional pivot is deferred.

---

## 2. Data model

```typescript
interface PivotGridDataField {
  field: string
  title?: string
  aggregate?: 'sum' | 'count' | 'average' | 'min' | 'max'
  format?: (value: number) => string
}

interface PivotGridProps {
  data: Array<Record<string, unknown>>
  columns: PivotGridDataField[]
  rows: PivotGridDataField[]
  measures: PivotGridDataField[]
  showGrandTotal?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `data` | `Array<Record<string, unknown>>` | _required_ | Flat row data to pivot. Each record is a plain object. |
| `columns` | `PivotGridDataField[]` | _required_ | Column dimension definitions. Only `columns[0]` is used in v1 (single column dimension). |
| `rows` | `PivotGridDataField[]` | _required_ | Row dimension definitions. Only `rows[0]` is used in v1 (single row dimension). |
| `measures` | `PivotGridDataField[]` | _required_ | Measure definitions. Only `measures[0]` is used in v1. |
| `showGrandTotal` | `boolean` | `true` | When `true`, a "Total" row and column are appended with grand totals. |
| `className` | `string` | `undefined` | Additional classes on the outer scroll container. |

### 3.1 `PivotGridDataField` fields

| Field | Type | Default | Meaning |
|---|---|---|---|
| `field` | `string` | _required_ | The property name to read from each data record. |
| `title` | `string` | `undefined` | Display label for the dimension/measure header. Falls back to `field` if absent. |
| `aggregate` | `'sum' \| 'count' \| 'average' \| 'min' \| 'max'` | `'sum'` | Aggregation function applied to the measure values. |
| `format` | `(value: number) => string` | `v => v.toLocaleString(...)` | Formats the aggregated cell value for display. Default is locale-formatted number with ≤2 decimal places. |

### 3.2 Empty / unconfigured state

When any of `columns`, `rows`, or `measures` is an empty array, PivotGrid renders a placeholder:

```
Configure rows, columns, and measures
```

No table is rendered.

### 3.3 Aggregation semantics

The built-in aggregation functions:

| `aggregate` | Behaviour |
|---|---|
| `sum` | Sum of all numeric values in the cell's data slice |
| `count` | Count of records in the cell's data slice |
| `average` | Mean of all numeric values; returns `0` for empty slice |
| `min` | Minimum numeric value; `Infinity` for empty (Math.min behaviour) |
| `max` | Maximum numeric value; `-Infinity` for empty (Math.max behaviour) |

Non-numeric values are filtered out (`isNaN` check).

---

## 4. Events

PivotGrid has **no events**. It is a read-only display component.

---

## 5. Slots

PivotGrid has no slot props.

---

## 6. Component composition

- **Financial reports.** PivotGrid renders GL summaries (account × period × amount).
- **Property analytics.** Revenue by property × month in a dashboard.
- **Work order summaries.** Count by category × assignee.

---

## 7. Deferred features

- **Multi-dimensional pivot** — multiple row dimensions / column dimensions / measures. v1 uses only `[0]` from each array. Full multi-dim pivot deferred.
- **Drill-down** — clicking a cell to filter the source data. Deferred; component emits no click events.
- **Column/row expand-collapse** — hierarchical dimension grouping. Deferred.
- **Export to CSV** — PivotGrid has no built-in export. Host uses ExportCsvButton externally.
- **Sorting** — sorting rows or columns by dimension value or total. Deferred.
