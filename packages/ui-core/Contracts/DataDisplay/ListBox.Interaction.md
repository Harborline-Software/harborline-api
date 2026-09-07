# ListBox — Interaction Contract

- **Component:** ListBox
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListBox.Semantic.md) · [Accessibility](./ListBox.Accessibility.md) · [Styling](./ListBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListBox.tsx`
- **Catalog row:** #77 ListBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Selection behavior

### Single selection

```
click item (enabled, not selected) → selected = [item.value]
click item (enabled, already selected) → selected = []
click item (disabled) → no-op
```

### Multiple selection

```
click item (enabled, not selected) → selected = [...selected, item.value]
click item (enabled, already selected) → selected = selected.filter(v ≠ item.value)
click item (disabled) → no-op
```

---

## 2. Drag-to-reorder (draggable=true)

```
dragstart on item[i] → draggingIdx = i
dragover on item[j] → e.preventDefault()
drop on item[j] (j ≠ draggingIdx) → splice item from i to j → setItems(newItems); onDragEnd(newItems)
dragend → draggingIdx = null
```

Disabled items are not draggable (`draggable={draggable && !item.disabled}`).

---

## 3. Toolbar operations

| Button | Action |
|---|---|
| Move Up | For each selected index (ascending), swap with the index above (skip if at position 0) |
| Move Down | For each selected index (descending), swap with the index below (skip if at bottom) |
| Remove | Remove all selected items; clear selection; call `onValueChange([])` and `onDragEnd(newItems)` |

Multiple selected items move together: if two adjacent selected items are moved up, they shift as a unit without passing each other.

---

## 4. No keyboard selection in M1

Items are click-only for selection. No Shift+click range-select or keyboard arrow navigation between items.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LB1 | High | No keyboard navigation between items — AT users cannot navigate the list | Accepted-risk M1 |
| G-LB2 | Medium | No Shift+click range selection for multi-select | Accepted-risk M1 |
| G-LB3 | Low | Drag-and-drop is mouse-only; no keyboard reorder equivalent | Accepted-risk M1; toolbar provides keyboard reorder path |
