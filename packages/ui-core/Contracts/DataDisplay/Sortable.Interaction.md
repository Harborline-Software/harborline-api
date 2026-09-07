# Sortable — Interaction Contract

- **Component:** Sortable
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sortable.Semantic.md) · [Accessibility](./Sortable.Accessibility.md) · [Styling](./Sortable.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Sortable.tsx`
- **Catalog row:** #121 Sortable (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
IDLE
  → dragstart on item[i] → DRAGGING(i)

DRAGGING(i)
  → dragover on item[j] → setOverIndex(j)
  → drop on item[j] (j ≠ i) → splice i to j → setItems(next) → onDragEnd(next) → IDLE
  → drop on item[i] (same item) → no-op → IDLE
  → dragend → IDLE (reset dragIndex, overIndex)
```

---

## 2. Drag mechanics

HTML5 drag-and-drop. `dragIndex` is stored in a ref (not state) to avoid triggering re-renders during drag. `overIndex` is stored in state to trigger visual feedback re-renders.

`onDragOver` calls `e.preventDefault()` to allow drops.

---

## 3. No keyboard reorder in M1

All items are `draggable` — pointer-only. No keyboard equivalent.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SO1 | High | No keyboard drag-to-reorder — AT users cannot reorder items | Accepted-risk M1 |
| G-SO2 | Low | `animation` prop accepted but no real animation | Accepted-risk M1 |
