# ListView — Accessibility Contract

- **Component:** ListView
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListView.Semantic.md) · [Interaction](./ListView.Interaction.md) · [Styling](./ListView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListView.tsx`
- **Catalog row:** #78 ListView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

No ARIA attributes added by ListView itself. The outer wrapper `<div>` and items `<div>` are plain elements. Semantic meaning (list, listitem, grid, etc.) is entirely caller-controlled via `itemRender`.

Pagination is handled by `<Pager>` which provides its own ARIA (see Pager contracts).

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LV2 | Medium | No `role="list"` or `role="grid"` on the list container — AT reads it as a generic div | Accepted-risk M1 |
| G-LV3 | Low | No `aria-label` on the list container — AT cannot identify the list's purpose | Accepted-risk M1 |
