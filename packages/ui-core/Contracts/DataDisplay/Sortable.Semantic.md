# Sortable — Semantic Contract

- **Component:** Sortable
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Sortable.Interaction.md) · [Accessibility](./Sortable.Accessibility.md) · [Styling](./Sortable.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Sortable.tsx`
- **Catalog row:** #121 Sortable (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled drag-and-drop list

---

## 1. Component purpose

**Sortable** — a drag-to-reorder list container. Wraps arbitrary item content via a render prop. Items can be dragged to new positions; the reordered array is returned via `onDragEnd`.

---

## 2. Props

```typescript
interface SortableProps {
  idField?: string                                        // default: 'id'; key field for React keys
  data: Array<Record<string, unknown>>                   // required; array of items
  itemRender: (item: Record<string, unknown>, index: number) => React.ReactNode
  onDragEnd?: (newOrder: Array<Record<string, unknown>>) => void
  animation?: boolean  // reserved; M1 not implemented
  className?: string
}
```

---

## 3. Item identity

Each item's React `key` is `String(item[idField] ?? index)`. When `idField` is absent from an item, falls back to array index — this is unstable and should be avoided.

---

## 4. Data synchronization

Local item state is initialized from `data` and re-synced via `useEffect` when `data` changes. Reorder operations mutate local state immediately and fire `onDragEnd` with the new order.

---

## 5. animation prop

`animation` is accepted but not used in M1. No CSS transitions beyond `transition-transform` on items.
