# TreeView — Interaction Contract

- **Component:** TreeView
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeView.Semantic.md) · [Accessibility](./TreeView.Accessibility.md) · [Styling](./TreeView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/TreeView.tsx`
- **Catalog row:** #142 TreeView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Pointer interactions

| Action | Condition | Effect |
|---|---|---|
| Click node row | `!node.disabled` | `onSelect?.(node.id)` |
| Click expand/collapse button | `hasChildren` | `toggleExpanded(node.id)`; `e.stopPropagation()` prevents row selection |
| Click disabled node | `node.disabled` | No-op (`pointer-events-none` via CSS) |

---

## 2. Keyboard interactions (per node row)

| Key | Condition | Effect |
|---|---|---|
| `Enter` or `Space` | Any focusable node | `onSelect?.(node.id)`; `e.preventDefault()` |
| `ArrowRight` | `hasChildren && !isExpanded` | `onToggle(node.id)` to expand; `e.preventDefault()` |
| `ArrowLeft` | `isExpanded` | `onToggle(node.id)` to collapse; `e.preventDefault()` |

The expand/collapse toggle button has `tabIndex=-1`; it is not keyboard reachable directly — keyboard expand/collapse is via the row's `ArrowRight`/`ArrowLeft` handlers.

---

## 3. Focus model

Each node row `<div>` has `tabIndex={disabled ? -1 : 0}`. Focus navigates node-by-node via Tab/Shift+Tab across all rendered (non-disabled) rows.

No `roving tabIndex` — every enabled node is in the natural tab order simultaneously.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TVIEW1 | High | No `ArrowUp`/`ArrowDown` — WAI-ARIA tree pattern requires sibling navigation with arrow keys | Accepted-risk M1 |
| G-TVIEW2 | Medium | No `Home`/`End` keys to jump to first/last node | Accepted-risk M1 |
| G-TVIEW3 | Medium | All enabled nodes are in tab order simultaneously (not roving tabIndex) — Tab traversal through large trees is slow | Accepted-risk M1 |
| G-TVIEW4 | Low | No `defaultSelected` — selection cannot be seeded without controlling it from the parent | Accepted-risk M1 |
| G-TVIEW5 | Low | Expand prop is hybrid controlled/uncontrolled — internal state updates even when prop is provided | Accepted-risk M1 |

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeView
audit rows 1-4. Prop/data-model shapes are owned by Semantic §FS-*; this contract owns the
user-visible state transitions, edge cases, and keyboard extensions.

---

### §FS-1 Wave — multi-select + checkbox interactions (audit rows 1, 3)

#### §FS-1.1 Checkbox interactions

Reference: Semantic §FS-1.1.

1. **Checkbox click.** Clicking the checkbox cell (or pressing Space when the checkbox has
   focus) toggles `node.checked` and fires `onCheckChange(node.id, !node.checked)`.
2. **Checkbox vs row-click independence.** Clicking the label row fires `onSelect` (or
   `onSelectionChange`); clicking the checkbox fires `onCheckChange`. Both may fire from the
   same event target only when the checkbox is the only interactive element in the row.
3. **Indeterminate rendering.** The browser renders a distinct `indeterminate` glyph when
   `node.indeterminate === true` and `node.checked === false`. Clicking an indeterminate
   checkbox fires `onCheckChange(node.id, true)` (moves to fully-checked); callers cascade
   children accordingly via `processTreeViewItems` (Semantic §FS-2.4).
4. **Disabled checkboxes.** `node.disabled === true` prevents click events on the checkbox;
   the checkbox renders with `disabled` attribute. `onCheckChange` does not fire.

**wave-3**

---

#### §FS-1.2 Multi-select keyboard + pointer (closes audit row 3)

Reference: Semantic §FS-1.2.

**Pointer behaviour:**

1. `selection === 'single'` (default) — clicking a node calls `onSelect?.(id)` (M1 behaviour).
2. `selection === 'multiple'` — clicking a node **sets** the selection to `[id]` (replaces
   prior selection). `onSelectionChange([id])` fires.
3. Ctrl+click in multiple mode **toggles** `id` in the selection set without clearing other
   entries. `onSelectionChange(nextIds)` fires.
4. Shift+click in multiple mode extends the selection from the anchor node (last directly
   clicked node without modifier) to the clicked node in tree-visible order.
   `onSelectionChange(rangeIds)` fires.
5. `selection === 'none'` — no selection events fire on click.

**Keyboard — Ctrl+Enter (closes audit row 3):**

6. When `selection === 'multiple'`, Ctrl+Enter on a focused node toggles that node in the
   selection set without moving focus. `onSelectionChange(nextIds)` fires.

**wave-3**

---

### §FS-2 Wave — drag-drop behaviour (audit row 2)

Reference: Semantic §FS-2.1.

1. **Drag start.** Pressing and holding the pointer on a `draggable` node's row (anywhere,
   or on the explicit drag handle if rendered) initiates a drag. A `TreeViewDragClue` element
   (semi-transparent label copy) follows the pointer.
2. **Drop target highlighting.** As the clue moves over other node rows:
   - Upper third of a row → "before" indicator (line above the row)
   - Lower third → "after" indicator (line below the row)
   - Middle third of a branch node → "inside" indicator (row highlight)
3. **Drop.** Releasing the pointer fires `onDragEnd({ sourceId, targetId, position })` on
   the tree instance that received the drop. TreeView does NOT mutate `nodes`.
4. **Cross-tree drop.** `onDragEnd` fires on the TARGET tree with `sourceId` from the source
   tree. The source tree receives no separate event — the caller manages both trees' data.
5. **Cancel (Escape).** Pressing Escape during an active drag cancels the operation. No
   `onDragEnd` fires; the clue disappears.
6. **Disabled nodes.** Disabled nodes are NOT draggable (drag cannot be initiated on them).
   They ARE valid drop targets.
7. **Roving tabIndex interaction.** Drag does not change the active (tabIndex=0) node; focus
   returns to the source node after Escape or on cancellation.

**wave-4**

---

### §FS-3 Wave — full keyboard matrix + roving tabIndex (audit row 4; closes G-TVIEW1/2/3)

Reference: Semantic §FS-3.

All seven new keys in the Semantic §FS-3 keyboard matrix require the following state-transition
rules:

1. **ArrowDown.** Determine the next visible node in pre-order tree traversal (first child if
   expanded, else next sibling, else parent's next sibling — standard tree order). Move the
   roving `tabIndex` to that node and call `element.focus()`.
2. **ArrowUp.** Determine the previous visible node in pre-order traversal (last expanded
   descendant of prior sibling, or prior sibling, or parent). Move the roving `tabIndex` and
   focus.
3. **ArrowRight on expanded branch.** Move focus to first child node (does not change expanded
   state).
4. **ArrowLeft on collapsed or leaf node.** Move focus to the parent node. If already a root
   node, no-op.
5. **Home.** Move focus to the very first root-level node.
6. **End.** Walk the full visible tree in pre-order to find the last visible node. Move focus
   there.
7. **Typeahead.** On any printable character key, search forward from the current active node
   through visible nodes (wrapping at the end) for the first node whose `label` starts with
   that character (case-insensitive). Move focus to that node. Multiple rapid presses of the
   same character cycle through all matching nodes.

**Focus management rules:**

- All arrow-key, Home, End, and typeahead focus transitions `e.preventDefault()` to prevent
  page scrolling.
- The "active node" (the one with `tabIndex={0}`) updates on every focus transition.
- If the active node is removed from the tree (data change), the active node resets to the
  first root node.
- Programmatic `focus()` called on the `<ul role="tree">` element moves focus to the active
  node (the one with `tabIndex={0}`).

**wave-3**
