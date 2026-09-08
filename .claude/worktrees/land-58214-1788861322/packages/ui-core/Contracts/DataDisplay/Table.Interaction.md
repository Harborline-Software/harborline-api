# Table — Interaction Contract

- **Component:** Table (family)
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Table.Semantic.md) · [Accessibility](./Table.Accessibility.md) · [Styling](./Table.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Table.tsx`
- **Catalog row:** Table (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Interaction model

The Table family is **display-only by design**. It ships **no** sorting, paging, selection,
filtering, row expansion, inline editing, drag-reorder, or virtualization. There are no event
handlers or keyboard behaviors beyond what the native elements provide.

This is the explicit boundary against {@link DataGrid}: when a table needs any interactive feature
or large-data performance, use DataGrid — do not grow those features onto Table.

---

## 2. Caller-supplied interactivity

Callers may attach their own handlers (`onClick`, etc.) to any part via `...rest` — e.g. a clickable
row — but Table provides no affordance, focus management, or ARIA for it. If a row is made
interactive, the caller owns the accessibility (see Accessibility contract §3).

---

## 3. Known gaps

- No keyboard grid navigation (arrow-key cell traversal) — that is a DataGrid concern.
