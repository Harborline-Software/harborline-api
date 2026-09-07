# PivotGrid — Accessibility Contract

- **Component:** PivotGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PivotGrid.Semantic.md) · [Interaction](./PivotGrid.Interaction.md) · [Accessibility](./PivotGrid.Accessibility.md) · [Styling](./PivotGrid.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/PivotGrid.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PivotGrid renders a complex HTML `<table>` with column headers, row headers, and data cells. Complex tables require ARIA headers association so screen readers can announce the relevant row/column headers for each data cell.

---

## 2. Table structure

```html
<div class="overflow-auto">
  <table class="text-sm border-collapse w-full">
    <thead>
      <tr>
        <th>{rowField.title}</th>        <!-- row dimension label -->
        <th>{colValue}</th>             <!-- one per column value -->
        [<th>Total</th>]                <!-- if showGrandTotal -->
      </tr>
    </thead>
    <tbody>
      <tr>
        <td>{rowValue}</td>             <!-- row header value -->
        <td>{aggregated value}</td>     <!-- data cells -->
        [<td>{rowGrandTotal}</td>]
      </tr>
      [<tr> grand total row </tr>]
    </tbody>
  </table>
</div>
```

---

## 3. ARIA table requirements

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships; WCAG 2.2 SC 4.1.2 Name, Role, Value.

### 3.1 Column headers

Column headers (`<th>` in `<thead>`) should have `scope="col"` to associate them with their column's data cells.

**Known gap (A1):** `scope="col"` is missing on all `<th>` elements. SR cannot reliably associate column values with their headers.

### 3.2 Row headers

The first `<td>` in each row (the row dimension value, e.g., property name) should be a `<th scope="row">` element, not `<td>`.

**Known gap (A2):** Row dimension cells are `<td>` not `<th scope="row">`. SR cannot reliably announce the row label for each data cell.

### 3.3 Table caption

The table has no `<caption>`. SR users have no textual description of what the pivot table represents.

**Known gap (A3):** No `<caption>` or `aria-label` on the table. Hosts should add one: `<table aria-label="Revenue by property and month">`.

---

## 4. Cell value accessibility

Aggregated numeric values render as locale-formatted strings (e.g., `"1,234.56"`). SR reads these as text. No additional ARIA is needed for numeric cells.

Grand total cells render in a `<tr>` with no special ARIA — SR reads them as part of the table flow.

---

## 5. Keyboard

PivotGrid is non-interactive. No keyboard navigation contract applies within the table itself. The table is a standard `<table>` element — standard browser table navigation applies when the table is focusable (Tab moves to cells if the table has `tabindex`, which it does not currently).

---

## 6. Color contrast

Data cells use alternating row backgrounds:

- Even rows: `bg-background` (white)
- Odd rows: `bg-muted/10` (light gray)
- Grand total row: `bg-muted/40`

Cell text is `text-sm` (implicit foreground). All combinations meet WCAG 2.2 SC 1.4.3 at standard font sizes assuming default foreground/background token values.

---

## 7. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | Missing `scope="col"` on `<th>` column headers | High | Add `scope="col"` to all `<th>` in `<thead>` |
| A2 | Row dimension cells are `<td>` not `<th scope="row">` | High | Change row-label cells to `<th scope="row">` |
| A3 | No table caption or `aria-label` | Medium | Host adds `aria-label` prop OR component adds a `caption?: string` prop |
| A4 | Overflow-scroll container has no accessible indication | Low | Add `aria-label="Scrollable table"` or visual scroll indicator |
