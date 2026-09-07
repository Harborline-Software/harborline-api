# Transfer — Interaction Contract

- **Component:** Transfer / ShuttleList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Transfer.Semantic.md) · [Accessibility](./Transfer.Accessibility.md) · [Styling](./Transfer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 Transfer / ShuttleList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Transfer baseline)

---

## 1. Item selection

Click an item in either panel to select it. Holding Shift+Click selects a range. Multiple items can be selected across the same panel.

---

## 2. Transfer operation

Clicking the "move right" button (→) moves all currently selected items in the left panel to the right panel.
Clicking the "move left" button (←) moves all currently selected items in the right panel to the left panel.
`onChange(newTargetKeys, direction, movedKeys)` fires.

---

## 3. Select all

A "select all" checkbox header selects or deselects all items in that panel. Indeterminate when some (but not all) items are selected.

---

## 4. Search filtering

When `showSearch=true`, typing in the search input filters visible items in that panel. Does not affect `targetKeys` or selection state.

---

## 5. Disabled items

Items with `disabled=true` cannot be selected or moved.

---

## 6. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
