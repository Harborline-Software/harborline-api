# TreeView — Semantic Contract

- **Component:** TreeView
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TreeView.Interaction.md) · [Accessibility](./TreeView.Accessibility.md) · [Styling](./TreeView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/TreeView.tsx`
- **Catalog row:** #142 TreeView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled recursive tree list

---

## 1. Component purpose

**TreeView** — a hierarchical tree of selectable, expandable nodes. Supports icons, disabled nodes, controlled selection, and controlled/uncontrolled expand state.

---

## 2. Data model

```typescript
interface TreeNode {
  id: string
  label: string
  icon?: React.ReactNode
  children?: TreeNode[]
  disabled?: boolean
}
```

Nodes with `children` are branch nodes; nodes without are leaf nodes.

---

## 3. Props

```typescript
interface TreeViewProps {
  nodes: TreeNode[]
  selected?: string          // controlled — ID of selected node
  expanded?: string[]        // semi-controlled — array of expanded IDs (see §4)
  onSelect?: (id: string) => void
  onExpandedChange?: (expanded: string[]) => void
  className?: string
}
```

---

## 4. State model

**Selection:** Always controlled. The parent provides `selected` (the ID string) and handles `onSelect`. No `defaultSelected` prop.

**Expanded set:** Hybrid controlled/uncontrolled.
- Internal `expandedState` initializes from `expandedProp ?? []`.
- Active `expanded` set = `expandedProp ?? expandedState` — if prop is provided it wins; otherwise internal state is used.
- `toggleExpanded` always updates internal state AND fires `onExpandedChange`.
- When `expanded` prop is omitted the component manages expand state internally.

---

## 5. Events

| Event | Trigger | Payload |
|---|---|---|
| `onSelect` | Node clicked or Enter/Space pressed | `id: string` |
| `onExpandedChange` | Branch node toggled | `expanded: string[]` (updated array) |

Disabled nodes suppress `onSelect` (click guard: `if (!node.disabled)`).

---

## 6. Rendering

Root renders `<ul role="tree">`. Each node is a `<li role="treeitem">` containing a `<div>` (the interactive row) and optionally a `<ul role="group">` for children (rendered only when expanded).

The component recurses via the internal `TreeNodeItem` function (not exported).

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeView
audit rows 1-7. All P1 gaps and P2 gaps are specced below. FR-1/FR-2/FR-3 rulings apply
(see `_shared/design/polish/family-rulings-2026-06-11.md`).

---

### §FS-1 Wave — checkboxes + multi-select + tri-state (audit row 1 + row 3)

#### §FS-1.1 Checkbox mode

Adds an opt-in checkbox column and tri-state indeterminate support to `TreeNode` and
`TreeViewProps`.

```typescript
// Extended TreeNode — fields added for checkbox mode; existing fields unchanged.
interface TreeNode {
  id: string
  label: string
  icon?: React.ReactNode
  children?: TreeNode[]
  disabled?: boolean
  // New — checkbox mode:
  checked?: boolean            // controlled checked state for this node
  indeterminate?: boolean      // tri-state: checked=false + indeterminate=true → mixed
}

// New TreeViewProps additions:
interface TreeViewProps {
  // ... existing props ...

  // Checkbox mode
  checkboxes?: boolean         // default false; renders checkbox per node
  onCheckChange?: (id: string, checked: boolean) => void
  // When omitted, tree manages no internal check state — callers update TreeNode.checked directly.
}
```

**Semantics:**

- When `checkboxes === true`, each node row prepends a checkbox before the expand toggle. The
  checkbox reflects `node.checked`. `node.indeterminate === true` (with `node.checked === false`)
  renders the checkbox in indeterminate state (native `HTMLInputElement.indeterminate`).
- Clicking the checkbox fires `onCheckChange(node.id, !node.checked)`. The tree does NOT cascade
  check state to children — that is the caller's responsibility (Kendo's `processTreeViewItems`
  pattern; see §FS-2.3 below for the utility spec).
- Disabled nodes (`node.disabled === true`) render a disabled checkbox; `onCheckChange` is not
  fired.
- Checkbox click and row-click (selection) are independent events. Both may be supplied
  simultaneously.
- FR-1: checkbox renders `aria-required` only when wrapped in a `FormField` — TreeView itself does
  not add `aria-required` to node checkboxes.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `checkboxes` | `boolean` | `false` | Renders a checkbox per node. |
| `onCheckChange` | `(id: string, checked: boolean) => void` | — | Fires when a node checkbox is toggled. |

**wave-3**

---

#### §FS-1.2 Multi-select mode

```typescript
// New props:
selection?: 'none' | 'single' | 'multiple'  // default 'single' (preserves M1 behaviour)
selectedIds?: string[]                        // controlled; replaces scalar `selected` in multiple mode
onSelectionChange?: (ids: string[]) => void   // replaces scalar `onSelect` in multiple mode
```

**Semantics:**

- `'none'` — no selection; clicking rows has no selection effect. `onSelect` and `onSelectionChange`
  are both suppressed.
- `'single'` — the M1 default. `selected` (scalar) + `onSelect` (scalar) remain the canonical
  props for backward compatibility. `selectedIds` and `onSelectionChange` are also accepted in
  single mode (treated as an array of at most one entry).
- `'multiple'` — clicking a node toggles it in/out of the selection set. `selectedIds` is the
  canonical array form. Ctrl+click (see Interaction §FS-1.2) is the additive-toggle key in
  multiple mode.
- When `selection === 'multiple'`, the scalar `selected` and `onSelect` props are deprecated (they
  still fire for single-item compatibility; prefer `selectedIds` + `onSelectionChange`).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `selection` | `'none' \| 'single' \| 'multiple'` | `'single'` | Selection model. |
| `selectedIds` | `string[]` | — | Controlled selection set (array). |
| `onSelectionChange` | `(ids: string[]) => void` | — | Fires when the selection set changes. |

**wave-3**

---

### §FS-2 Wave — drag-drop + load-on-demand + item template (audit rows 2, 5, 6)

#### §FS-2.1 Drag-drop within and between trees

```typescript
export interface TreeViewDragEvent {
  sourceId: string        // id of the dragged node
  targetId: string        // id of the node it was dropped onto or adjacent to
  position: 'before' | 'after' | 'inside'
}

// New props:
draggable?: boolean                          // default false; enables drag handle on each node
onDragEnd?: (event: TreeViewDragEvent) => void
// Cross-tree drop: both trees must declare the same `dragGroup` string.
dragGroup?: string
```

**Semantics:**

- When `draggable === true`, each node row renders a drag handle (grip icon, `aria-hidden`). The
  node can be dragged via the handle or the full row.
- During drag, a `TreeViewDragClue` element (semi-transparent copy of the node label) follows the
  pointer.
- **Position indicators:**
  - "before" — drop indicator line above the target node
  - "after" — drop indicator line below the target node
  - "inside" — drop indicator as a highlight on the target row (target becomes parent)
- `onDragEnd` fires with `{ sourceId, targetId, position }`. TreeView does NOT mutate
  `nodes` — the caller is responsible for updating the data model.
- **Cross-tree drag:** when two `TreeView` instances share the same `dragGroup` string, dragging a
  node from one tree and dropping onto the other triggers `onDragEnd` on the **target** tree with
  `sourceId` from the source tree. The caller moves the node between data models.
- Disabled nodes are not draggable but are valid drop targets.
- Keyboard drag is not supported in this wave (pointer-only UX per DataGrid §FS-1.4 precedent).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `draggable` | `boolean` | `false` | Enables drag-drop reorder. |
| `onDragEnd` | `(event: TreeViewDragEvent) => void` | — | Fires after a successful drop. |
| `dragGroup` | `string` | — | Shared group key for cross-tree drag. |

**wave-4**

---

#### §FS-2.2 Load-on-demand (async children)

```typescript
// Extended TreeNode:
interface TreeNode {
  // ... existing fields ...
  hasChildren?: boolean   // true when the node has children that have not yet been loaded
}

// New TreeViewProps prop:
onExpandRequest?: (id: string) => void  // fires when a node with hasChildren=true is expanded
```

**Semantics:**

- A node with `hasChildren === true` and `children === undefined` renders the expand toggle
  (branch node affordance) even though `children` is absent.
- When the user expands such a node, TreeView fires `onExpandRequest(node.id)`. The caller is
  responsible for fetching children and updating `nodes` with the populated `children` array.
- While the caller is loading, the node remains expanded in the expand set; an empty `<ul
  role="group">` is rendered (callers may pass a loading-placeholder child node to indicate
  in-progress state).
- Once `children` is populated, the tree re-renders with the real children.
- `hasChildren === false` (explicit) suppresses the expand toggle even if `children` is an empty
  array.

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `onExpandRequest` | `(id: string) => void` | — | Fires when a lazy node is first expanded. |

`TreeNode.hasChildren?: boolean` is a new optional field on the data model.

**wave-4**

---

#### §FS-2.3 Item template (`itemRender`)

```typescript
export interface TreeItemRenderProps {
  node: TreeNode
  level: number
  isSelected: boolean
  isExpanded: boolean
}

// New TreeViewProps prop:
itemRender?: (props: TreeItemRenderProps) => React.ReactNode
```

**Semantics:**

- When `itemRender` is supplied, it replaces the default label + icon rendering inside each node
  row. The wrapping row `<div>` (with expand toggle, `tabIndex`, keyboard handlers, and
  selection/disabled classes) is retained — `itemRender` replaces only the content after the
  expand toggle.
- Callers use `itemRender` to add badges, action menus, status dots, or richly formatted labels.
- The `node` object passed includes the full `TreeNode` record (including any caller-defined extra
  fields on the type).

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `itemRender` | `(props: TreeItemRenderProps) => ReactNode` | — | Custom node content renderer; replaces label+icon. |

**wave-4**

---

#### §FS-2.4 `processTreeViewItems` utility (audit row 7)

A normalisation + cascade-check helper exported alongside TreeView (not a component prop).

```typescript
export interface ProcessTreeViewItemsOptions {
  expandedIds?: string[]
  selectedIds?: string[]
  // Cascade check: when a parent is checked, all children are checked.
  checkIds?: string[]        // ids to check
  cascade?: boolean          // default true; cascades check/uncheck to children
}

export function processTreeViewItems(
  nodes: TreeNode[],
  options?: ProcessTreeViewItemsOptions,
): TreeNode[]
```

**Semantics:**

- `processTreeViewItems` is a pure function that returns a new `nodes` array with `checked` and
  `indeterminate` fields populated based on `checkIds` and the cascade rules.
- When `cascade === true` (default): checking a parent marks all descendants `checked: true`;
  unchecking a parent marks all descendants `checked: false`; a parent whose children are
  partially checked gets `indeterminate: true`.
- Useful in `onCheckChange` handlers to derive the next full `nodes` state without manual
  recursion.
- Does not mutate the input array; returns new node objects (shallow clone at changed nodes).

**wave-4**

---

### §FS-3 Wave — full keyboard matrix + roving tabIndex (audit row 4; closes G-TVIEW1/2/3)

**Note:** audit row 4 is marked Level-A P1 — these are WCAG SC 2.1.1 (keyboard access) items.
All items in this section resolve existing gap IDs from the Interaction contract.

```typescript
// No new props — keyboard behaviour is a pure behaviour change to the existing
// implementation. The aria-label prop below closes G-TVIEW7.

// New optional prop:
aria-label?: string   // forwarded to <ul role="tree"> to close G-TVIEW7
```

**Keyboard matrix (full — includes existing keys for completeness):**

| Key | Context | Behaviour | Wave tag |
|---|---|---|---|
| Enter or Space | Any focused node | `onSelect?.(node.id)` + `e.preventDefault()` | existing |
| ArrowRight | Branch node, collapsed | Expand node (`toggleExpanded`); `e.preventDefault()` | existing |
| ArrowRight | Branch node, expanded | Move focus to first visible child | wave-3 NEW (closes G-TVIEW1) |
| ArrowLeft | Any node, expanded | Collapse node (`toggleExpanded`); `e.preventDefault()` | existing |
| ArrowLeft | Any node, collapsed or leaf | Move focus to parent node | wave-3 NEW (closes G-TVIEW1) |
| ArrowDown | Any focused node | Move focus to next visible node in tree order | wave-3 NEW (closes G-TVIEW1) |
| ArrowUp | Any focused node | Move focus to previous visible node in tree order | wave-3 NEW (closes G-TVIEW1) |
| Home | Any focused node | Move focus to the first root node | wave-3 NEW (closes G-TVIEW2) |
| End | Any focused node | Move focus to the last visible node in the tree | wave-3 NEW (closes G-TVIEW2) |
| Printable char | Any focused node | Move focus to next node whose label starts with that character (typeahead; wraps) | wave-3 NEW (closes audit row 4) |
| Ctrl+Enter | Multiple-mode node | Toggle node in selection set (§FS-1.2) | wave-3 NEW (closes audit row 3) |

**Roving tabIndex model (wave-3 — closes G-TVIEW3):**

The M1 model places `tabIndex={0}` on ALL enabled nodes. The roving model replaces this:

- Exactly ONE node has `tabIndex={0}` at any time — the "active node" (last focused node, or
  the `selected` node on mount, or the first node if nothing is selected).
- All other enabled nodes have `tabIndex={-1}`.
- When focus is programmatically moved (arrow keys, Home, End) the component sets
  `tabIndex={0}` on the new target node, `tabIndex={-1}` on the prior target node, and calls
  `element.focus()` on the new target.
- Tab / Shift+Tab moves focus OUT OF the tree widget to the next / previous focusable element
  in the document (standard roving tabIndex contract). A single Tab stop for the whole tree.

**wave-3**
