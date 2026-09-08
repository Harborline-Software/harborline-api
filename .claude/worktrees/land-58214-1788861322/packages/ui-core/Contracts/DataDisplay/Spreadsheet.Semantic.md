# Spreadsheet — Semantic Contract

- **Component:** Spreadsheet
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Spreadsheet.Interaction.md) · [Accessibility](./Spreadsheet.Accessibility.md) · [Styling](./Spreadsheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Spreadsheet.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled cell grid

---

## 1. Component purpose

**Spreadsheet** — a cells-based editable grid component modelled after a spreadsheet UI. Renders a scrollable grid of rows and columns with Excel-style column headers (A, B, C…) and numeric row headers. Supports single-cell selection, inline cell editing, formula prefix (`=`), locked cells, cell-level styling, and a formula bar. Intended for budget-entry, data-correction, and inline tabular editing surfaces.

---

## 2. Data model

```typescript
interface SpreadsheetCell {
  value?: string | number
  formula?: string
  format?: string          // reserved; not yet applied visually
  locked?: boolean
  style?: React.CSSProperties
}

type SpreadsheetData = Record<string, SpreadsheetCell>
// Keys are cell references: "A1", "B3", "Z26", etc.

interface SpreadsheetProps {
  data?: SpreadsheetData                                // controlled
  defaultData?: SpreadsheetData                        // uncontrolled seed; default: {}
  rows?: number                                         // default: 20
  columns?: number                                      // default: 10
  columnWidth?: number                                  // pixels; default: 100
  rowHeight?: number                                    // pixels; default: 28
  onCellChange?: (ref: string, cell: SpreadsheetCell) => void
  className?: string
}
```

---

## 3. Cell reference format

Cell references use the format `{column_letter}{row_number}`:

- Column: A–Z, then AA, AB, ... (base-26 with no zero; computed by `colLabel(n)`).
- Row: 1-indexed.

Examples: `A1`, `B2`, `Z20`, `AA1`.

---

## 4. Controlled vs uncontrolled

- **Controlled:** `data` prop provided. Cell edits fire `onCellChange(ref, cell)` but the host must update `data` to reflect them; the internal `data` state is not updated in controlled mode.
- **Uncontrolled:** `data` not provided. Internal `data` state is seeded from `defaultData` and updated on each edit.

The active cells source is `controlledData ?? data`.

---

## 5. Formula detection

When the user commits an edit value starting with `=`, the cell is stored as `{ formula: editValue, value: editValue }`. The formula string is displayed as-is (no evaluation engine). The formula bar shows the formula for the selected cell.

---

## 6. Formula bar

The formula bar at the top of the component shows:
- **Cell reference box** (left, fixed width 16): the currently selected cell reference (`selected ?? ''`).
- **Value/formula display** (right, flex-1): when editing, the live edit value; when not editing, the selected cell's formula (or value if no formula).

The formula bar is display-only — it is not an editable input.

---

## 7. Locked cells

Cells with `locked: true` cannot be edited. Double-click or `Enter`/`F2` on a locked cell is a no-op. Locked cells receive a muted background (`bg-muted/10`).

---

## 8. Related components

- **DataGrid / DataTable** — column-defined read/sort grid; Spreadsheet is cell-addressable editable.
- **DataEntry forms** — for structured single-record editing; Spreadsheet suits free-form tabular entry.
