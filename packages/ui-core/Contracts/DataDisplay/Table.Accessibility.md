# Table — Accessibility Contract

- **Component:** Table (family)
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Table.Semantic.md) · [Interaction](./Table.Interaction.md) · [Styling](./Table.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Table.tsx`
- **Catalog row:** Table (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Native table semantics

Because the family renders real `<table>` / `<thead>` / `<tbody>` / `<tr>` / `<th>` / `<td>` /
`<caption>` elements, it inherits the browser's native table accessibility model: AT exposes it as a
data table with row/column structure, header association, and cell navigation. No ARIA `role`
overrides are applied (and callers should not add `role="presentation"` — that would discard the
semantics this component exists to provide).

---

## 2. Header association

`TableHeaderCell` renders `<th>` with `scope="col"` by default; callers pass `scope="row"` for row
headers. This gives AT the column/row header association for each data cell. Tables that need a
programmatic name should use `TableCaption` (or an external `aria-labelledby`).

---

## 3. Caller-made-interactive rows

Table itself adds no interactive ARIA. If a caller makes a row clickable (via `...rest` handlers),
the caller is responsible for keyboard operability and an accessible name (e.g. a real link/button
inside a cell is preferred over a click handler on `<tr>`).

---

## 4. Contrast

Header text uses `muted-foreground`, body text uses `foreground`, separators use `border` tokens —
all meeting WCAG AA against the surface. The story set is exercised by the `@harborline-software/ui-react`
axe-core story-runner gate (ADR 0107).

---

## 5. Known gaps

- No built-in `aria-sort` — sorting is out of scope (DataGrid territory), so no sort state is
  communicated. This is intentional, not a defect.
