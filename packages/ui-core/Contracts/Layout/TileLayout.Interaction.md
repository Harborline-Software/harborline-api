# TileLayout — Interaction Contract

- **Component:** TileLayout
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TileLayout.Semantic.md) · [Accessibility](./TileLayout.Accessibility.md) · [Styling](./TileLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/TileLayout.tsx`
- **Catalog row:** #135 TileLayout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
IDLE
  → dragstart on tile[A] (reorderable) → DRAGGING(A)

DRAGGING(A)
  → dragover on tile[B] → setOverId(B)
  → drop on tile[B] (B ≠ A) → splice A to B's position → setItems(next) → onReorder(next) → IDLE
  → drop on tile[A] (same tile) → no-op → IDLE
  → dragend → IDLE (cleanup draggingId + overId)
```

---

## 2. Drag implementation

HTML5 drag-and-drop (`draggable`, `onDragStart`, `onDragOver`, `onDrop`, `onDragEnd`). `onDragOver` calls `e.preventDefault()` to allow drops.

Reorder is array splice: the dragged item is removed from its position and inserted at the drop target's position.

---

## 3. No resize in M1

`TileLayoutItem.resizable` is accepted but not used. Tiles cannot be resized by the user.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TLAYOUT1 | High | No keyboard drag-to-reorder | Accepted-risk M1 |
| G-TLAYOUT2 | Medium | `resizable` prop accepted but not implemented | Accepted-risk M1; noted in Semantic §2 |
| G-TLAYOUT3 | Low | Drop position is always "replace slot" — no visual insertion indicator | Accepted-risk M1 |
