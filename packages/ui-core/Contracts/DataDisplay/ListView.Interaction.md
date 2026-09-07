# ListView — Interaction Contract

- **Component:** ListView
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListView.Semantic.md) · [Accessibility](./ListView.Accessibility.md) · [Styling](./ListView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListView.tsx`
- **Catalog row:** #78 ListView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Pagination interaction

`<Pager>` fires `onPageChange(p: number)` → wrapped to `onPageChange({ page: p, pageSize: currentPage.pageSize })`.

Uncontrolled: `setInternalPage` updates local state. Controlled: only `onPageChange` fires; parent must update `page` prop.

---

## 2. Item interaction

None. `ListView` does not handle item clicks or selection. All interaction within items is caller-controlled via `itemRender`.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LV1 | Low | Items keyed by array index — reorder-sensitive reconciliation issues possible | Accepted-risk M1 |
