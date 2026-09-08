# Table — Styling Contract

- **Component:** Table (family)
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Table.Semantic.md) · [Interaction](./Table.Interaction.md) · [Accessibility](./Table.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Table.tsx`
- **Catalog row:** Table (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18; Shadcn/Radix Table baseline)

---

## 1. Table

`<table>` — `w-full caption-bottom border-collapse text-sm text-foreground`. Full-width,
collapsed borders, design-system foreground text token. `caption-bottom` places the caption below
the table (Shadcn convention).

---

## 2. Head

`<thead>` — `border-b border-border text-left text-muted-foreground`. A single bottom border under
the header row; muted, left-aligned header text.

---

## 3. Body

`<tbody>` — `divide-y divide-border/60`. Subtle row separators via `divide-y` (softer
`border/60` tone than the header rule).

---

## 4. Row

`<tr>` — `transition-colors`. No default background; callers add hover/selected affordances via
`className` (Table ships no interaction styling — see Interaction contract).

---

## 5. Cells

Density-driven padding shared from `Table` via context:

| `density` | header + data cell padding |
| --- | --- |
| `sm` | `px-3 py-1.5` |
| `md` (default) | `px-4 py-2` |

- Header cell (`<th>`): `font-medium` + density padding.
- Data cell (`<td>`): `align-middle` + density padding.

---

## 6. Caption

`<caption>` — `mt-2 text-xs text-muted-foreground`.

---

## 7. className passthrough

Every part merges the caller's `className` via `cn()` onto its native element, and forwards
`...rest` (including `colSpan`/`rowSpan`/`scope`) to that element.
