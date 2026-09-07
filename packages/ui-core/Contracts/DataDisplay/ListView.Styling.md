# ListView — Styling Contract

- **Component:** ListView
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListView.Semantic.md) · [Interaction](./ListView.Interaction.md) · [Accessibility](./ListView.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListView.tsx`
- **Catalog row:** #78 ListView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`flex flex-col gap-2` + `className` passthrough.

---

## 2. Items container

`flex flex-col` — wraps all rendered items. No additional styling; item appearance is entirely caller-controlled via `itemRender`.

---

## 3. Pager

Rendered below items container when `pageable=true`. Pager styling is defined in the Pager contracts.
