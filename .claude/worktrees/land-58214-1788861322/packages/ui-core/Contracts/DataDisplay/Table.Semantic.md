# Table — Semantic Contract

- **Component:** Table (family: Table, TableHead, TableBody, TableRow, TableHeaderCell, TableCell, TableCaption)
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Table.Interaction.md) · [Accessibility](./Table.Accessibility.md) · [Styling](./Table.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Table.tsx`
- **Catalog row:** Table (`app-priority: high`, `library-scope: in-scope`) — lightweight semantic table
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18 catalog-gap addition; Shadcn/Radix Table baseline)
- **Interaction-class:** display-only (static / small tables; no built-in interactivity)

---

## 1. Component purpose

**Table family** — lightweight, semantic styled wrappers over the native table elements
(`<table>`, `<thead>`, `<tbody>`, `<tr>`, `<th>`, `<td>`, `<caption>`). Design-system tokens and
sensible spacing; **NO sorting, paging, selection, filtering, or virtualization**. This is the
deliberately-simple counterpart to {@link DataGrid}: use Table for static or small tables where
native semantics are exactly right (provenance maps, key/value cascades, summary tables); reach for
DataGrid when interaction or large-data performance is needed.

The family carries **no domain concepts** — it is generic presentation only.

---

## 2. Parts and props

```typescript
interface TableProps extends React.TableHTMLAttributes<HTMLTableElement> {
  density?: 'sm' | 'md'   // cell padding density; default 'md'
}
interface TableHeadProps       extends React.HTMLAttributes<HTMLTableSectionElement> {}
interface TableBodyProps       extends React.HTMLAttributes<HTMLTableSectionElement> {}
interface TableRowProps        extends React.HTMLAttributes<HTMLTableRowElement> {}
interface TableHeaderCellProps extends React.ThHTMLAttributes<HTMLTableCellElement> {}  // scope defaults to 'col'
interface TableCellProps       extends React.TdHTMLAttributes<HTMLTableCellElement> {}
interface TableCaptionProps    extends React.HTMLAttributes<HTMLTableCaptionElement> {}
```

Every part forwards `...rest` and `ref` to its underlying native element, so callers can attach
`data-testid`, `aria-*`, `id`, `colSpan`/`rowSpan`, `scope`, and handlers directly.

---

## 3. Implementation strategy

Each part is a thin `forwardRef` wrapper that applies token-based classes (merged with the caller's
`className`) and renders the matching native element with `{...rest}` spread. `density` is shared
from `Table` down to header/data cells via a small React context so all cells pick up a consistent
padding scale without prop drilling.

---

## 4. Composition

Callers compose the native structure explicitly:

```tsx
<Table>
  <TableCaption>…</TableCaption>
  <TableHead>
    <TableRow><TableHeaderCell>Capability</TableHeaderCell>…</TableRow>
  </TableHead>
  <TableBody>
    <TableRow><TableCell>…</TableCell>…</TableRow>
  </TableBody>
</Table>
```

No data-prop / column-def API — that is DataGrid's territory. Table is markup-driven.
