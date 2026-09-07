# ListView — Semantic Contract

- **Component:** ListView
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ListView.Interaction.md) · [Accessibility](./ListView.Accessibility.md) · [Styling](./ListView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListView.tsx`
- **Catalog row:** #78 ListView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled list renderer

---

## 1. Component purpose

**ListView** — a generic list renderer with optional client-side pagination. Delegates item rendering to a render prop; provides no built-in item layout or selection. Pairs with `Pager` for pagination UI.

---

## 2. Props

```typescript
interface PageState {
  page: number
  pageSize: number
}

interface ListViewProps<T = unknown> {
  data: T[]
  itemRender: (item: T, index: number) => React.ReactNode  // required — caller controls item layout
  pageable?: boolean          // default: false
  pageSize?: number           // default: 10 — seed for default page state
  page?: PageState            // controlled pagination
  defaultPage?: PageState     // uncontrolled pagination seed
  onPageChange?: (page: PageState) => void
  className?: string
}
```

---

## 3. Pagination behavior

When `pageable=true`: slices `visibleData = data.slice((page-1)*pageSize, page*pageSize)`. Renders a `<Pager>` below the list. Page changes fire `onPageChange({ page, pageSize })`.

When `pageable=false`: renders all `data` items.

---

## 4. Controlled / uncontrolled

Controlled when `page` is provided. Uncontrolled uses `internalPage` seeded by `defaultPage ?? { page: 1, pageSize }`.

---

## 5. Generic typing

`ListView<T>` is generic. `itemRender` receives `(item: T, index: number)`.

---

## 6. Key strategy

Items are keyed by `index` — callers should be aware that index-based keys may cause React reconciliation issues if `data` order changes.
