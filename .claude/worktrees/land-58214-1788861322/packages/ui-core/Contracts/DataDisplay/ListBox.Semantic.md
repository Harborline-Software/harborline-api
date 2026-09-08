# ListBox — Semantic Contract

- **Component:** ListBox
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ListBox.Interaction.md) · [Accessibility](./ListBox.Accessibility.md) · [Styling](./ListBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListBox.tsx`
- **Catalog row:** #77 ListBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled listbox with keyboard navigation

---

## 1. Component purpose

**ListBox** — a scrollable list of selectable items with support for single and multiple selection, drag-to-reorder, and an optional toolbar with move-up/move-down/remove controls.

---

## 2. Props

```typescript
interface ListBoxItem {
  text: string
  value: string | number
  disabled?: boolean
}

interface ListBoxProps {
  data: ListBoxItem[] | string[]   // required; string[] is normalized to {text, value}
  value?: Array<string | number>   // controlled; selected values
  defaultValue?: Array<string | number>  // default: []
  onValueChange?: (values: Array<string | number>) => void
  selection?: 'single' | 'multiple'    // default: 'multiple'
  draggable?: boolean                  // default: false
  onDragEnd?: (items: ListBoxItem[]) => void
  toolbar?: boolean                    // default: false; shows move/remove buttons
  className?: string
}
```

---

## 3. Data normalization

`string[]` input is normalized to `ListBoxItem[]` via `{ text: d, value: d }`. Normalization runs on initial render and re-runs when `data` changes.

---

## 4. Selection model

`value` is a flat array of selected item `value` primitives.

- `selection='single'`: selecting an already-selected item deselects it (returns `[]`); selecting a new item replaces selection.
- `selection='multiple'`: clicking toggles the item in/out of the selection set.

---

## 5. Drag-to-reorder

When `draggable=true`, items have HTML5 drag-and-drop. Dragging an item over another swaps their positions. `onDragEnd` is called with the new item order after each reorder.

---

## 6. Toolbar

When `toolbar=true`, a column of buttons appears beside the list: Move Up (▲), Move Down (▼), Remove (×). These operate on the currently selected items.

Move Up/Down shifts selected items one position in the list. Remove removes selected items from the list and clears the selection.
