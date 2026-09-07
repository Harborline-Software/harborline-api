# OrgChart — Interaction Contract

- **Component:** OrgChart
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./OrgChart.Semantic.md) · [Accessibility](./OrgChart.Accessibility.md) · [Styling](./OrgChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A24 OrgChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik OrgChart baseline)

---

## 1. Node click

Click on a node → `onNodeClick(node)` fires. No default selection state; parent manages highlighting if needed.

---

## 2. Expand / collapse

Chevron button on nodes with children toggles subtree visibility. Animation: subtree fades + collapses over 200ms. Connector lines re-route on collapse.

---

## 3. Pan

When `pannable=true`, the canvas can be dragged via pointer. Mouse wheel + two-finger trackpad scroll moves the canvas.

---

## 4. Zoom

When `zoomable=true`, Ctrl+scroll (mouse) or pinch (touch) zooms the canvas. `defaultZoom` sets initial scale (default 1.0). Min zoom: 0.25. Max zoom: 3.0.

---

## 5. Keyboard navigation

Arrow keys move focus between nodes when the chart container has focus. `Enter` fires `onNodeClick`. `Space` toggles expand/collapse on nodes with children.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ORG1 | Medium | Pan gesture has no keyboard equivalent | Accepted-risk M1; keyboard arrow navigation handles node-to-node traversal; full pan not needed via AT |
| G-ORG2 | Low | Zoom has no keyboard shortcut | Deferred; browsers support Ctrl+scroll natively; chart zoom resets on focus out |

---

## Full-surface expansion (2026-06-11 — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — node selection and checkbox

**Status:** Draft

#### §FS-1.1 Selection model

```typescript
interface OrgChartCheckDescriptor {
  nodeId: string | number
  checked: boolean
  indeterminate?: boolean   // true when some but not all descendants are checked
}

// Additions to OrgChartProps:
selectable?: boolean                      // default: false; enables checkbox column
selectedIds?: Array<string | number>      // controlled selection
defaultSelectedIds?: Array<string | number>
onSelectionChange?: (ids: Array<string | number>) => void
selectionMode?: 'single' | 'multiple'    // default: 'multiple'
```

When `selectable === true`, each node renders a checkbox at its leading edge (before
the avatar/title content). The checkbox carries `aria-checked` and `role="checkbox"`.

**Single mode:** clicking a node's checkbox selects that node and deselects all others.
Clicking the already-selected node's checkbox deselects it (empty selection allowed).
No header / select-all checkbox in single mode.

**Multiple mode:** clicking a node's checkbox toggles that node. A select-all checkbox
is NOT rendered at the top level (OrgChart has no canonical "header row" for it).
The host may render a select-all control outside the OrgChart and manage selection
via the `selectedIds` controlled prop.

#### §FS-1.2 Subtree propagation

When a non-leaf node's checkbox is clicked in multiple mode:
- Checking it recursively checks all visible descendants.
- Unchecking it recursively unchecks all visible descendants.
- A node whose descendants are partially checked renders its checkbox in an
  `indeterminate` state (via `HTMLInputElement.indeterminate`).

`onSelectionChange` fires with the flat list of all individually-selected node ids
after propagation (descendant ids are included; parent ids that are fully-selected are
also included).

#### §FS-1.3 Keyboard

Tab navigates between node checkboxes. Space toggles the focused checkbox.

**wave-2**

---

### §FS-2 Wave-2 — node editing (add / remove / edit)

**Status:** Draft

#### §FS-2.1 Operations model

```typescript
type OrgChartOperation = 'add-child' | 'edit' | 'remove'

interface OrgChartActionEvent {
  operation: OrgChartOperation
  node: OrgNode
  parentNode?: OrgNode    // present only for 'add-child'
}

// Additions to OrgChartProps:
editable?: boolean          // default: false; enables edit controls
onAction?: (event: OrgChartActionEvent) => void
```

When `editable === true`, each node receives an action menu affordance (ellipsis icon
button `···`) that appears on hover (and on focus for keyboard users).

#### §FS-2.2 Action menu

The action menu is a popup (adopts FR-2 `open` / `onOpenChange` controlled pattern).
Menu items:

| Item | Fires |
|---|---|
| Add child | `onAction({ operation: 'add-child', node, parentNode: node })` |
| Edit | `onAction({ operation: 'edit', node })` |
| Remove | `onAction({ operation: 'remove', node })` (disabled on the root node) |

The OrgChart does NOT provide inline editing UI. `onAction` is called and the host
is responsible for opening an edit dialog, updating the `data` prop, etc.
(Same pattern as TaskBoard `onCardEdit`.)

#### §FS-2.3 Add-child flow

After `onAction({ operation: 'add-child' })` is handled and the host appends a new
child to `data`, the new child node enters `expanded === true` and scrolls into view
(if the chart is pannable).

#### §FS-2.4 Remove behavior

`operation: 'remove'` fires before the node is removed from `data`. The OrgChart
does not remove the node itself — the host updates `data`.

The root node's action menu omits the "Remove" item (root cannot be removed).

**wave-2**

---

### §FS-3 Wave-3 — node grouping

**Status:** Draft

#### §FS-3.1 Grouping model

Kendo OrgChart supports grouping sibling nodes with the same parent into a visual
cluster (a horizontal group strip instead of individual branches).

```typescript
// Addition to OrgNode:
interface OrgNode {
  // existing fields...
  group?: string   // nodes with same parent AND same `group` value are clustered
}
```

When multiple siblings share a `group` value, they are rendered as a horizontal
strip connected to the parent by a single vertical line, then fanning out horizontally.
The group strip is collapsible as a unit (one collapse control for the whole group).

#### §FS-3.2 Group collapse behavior

Collapsing a group strip fires `onNodeExpand(groupRepresentativeNode, false)` where
the representative node is the first member of the group (alphabetical by id if not
ordered). The `node.expanded` value on the representative node is used to determine
group expanded state.

All nodes in the group share the collapsed / expanded state — they cannot be
individually collapsed.

#### §FS-3.3 Group rendering

The group strip renders each node at a fixed width with equal horizontal spacing.
Long labels truncate with an ellipsis; full text appears on hover via `title` attribute.
Custom `nodeRender` applies to each node within the group individually.

**wave-3**
