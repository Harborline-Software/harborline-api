# Sortable — Accessibility Contract

- **Component:** Sortable
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sortable.Semantic.md) · [Interaction](./Sortable.Interaction.md) · [Styling](./Sortable.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Sortable.tsx`
- **Catalog row:** #121 Sortable (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="list"` | Container `<div>` | ARIA list |
| `role="listitem"` | Each item `<div>` | ARIA list item |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SO3 | Critical | No `aria-grabbed`, `aria-dropeffect` or equivalent drag-and-drop ARIA; AT users have no awareness of reorder capability or drag state | Accepted-risk M1; HTML5 DnD API has limited AT support |
| G-SO4 | High | No keyboard drag alternative — keyboard users cannot reorder | Accepted-risk M1 |
| G-SO5 | Medium | No `aria-label` on list or items describing reorder capability | Accepted-risk M1 |
