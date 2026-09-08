# TreeView — Accessibility Contract

- **Component:** TreeView
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeView.Semantic.md) · [Interaction](./TreeView.Interaction.md) · [Styling](./TreeView.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/TreeView.tsx`
- **Catalog row:** #142 TreeView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="tree"` | Root `<ul>` | WAI-ARIA tree widget |
| `role="treeitem"` | Each node `<li>` | Tree node |
| `aria-selected` | `<li>` | `true` when node is selected, `false` otherwise |
| `aria-expanded` | `<li>` (branch nodes only) | `true` when expanded, `false` when collapsed; omitted on leaf nodes |
| `role="group"` | Children `<ul>` | Subtree group |
| `aria-hidden="true"` | Expand/collapse toggle `<button>` | Hidden from AT; keyboard expand via row key handlers |
| `aria-hidden="true"` | Leaf spacer `<span>` | Visual indent placeholder; hidden from AT |

---

## 2. Focus

Node row `<div>` carries `tabIndex={0}` for enabled nodes, `tabIndex={-1}` for disabled nodes. All enabled nodes are simultaneously in the tab order (no roving tabIndex — see G-TVIEW3).

Focus ring: `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500` (hardcoded blue — see G-TVIEW6).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TVIEW3 | Medium | All enabled nodes in tab order simultaneously — not roving tabIndex; large trees produce many tab stops | Accepted-risk M1 |
| G-TVIEW6 | Low | Focus ring uses hardcoded `ring-blue-500` instead of `ring-ring` design token | Accepted-risk M1 |
| G-TVIEW7 | Low | No `aria-label` on the root `<ul role="tree">` — AT announces as unnamed tree | Accepted-risk M1 |
| G-TVIEW8 | Low | No `aria-disabled` on disabled node `<li>` — disabled state expressed only via `tabIndex=-1` and visual opacity | Accepted-risk M1 |

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeView
audit row 4 (full keyboard matrix — Level-A P1). All items below are WCAG Level A unless
noted. FR-2 applies (see `_shared/design/polish/family-rulings-2026-06-11.md`).

---

### §FS-1 Wave — roving tabIndex + full ARIA tree pattern (audit row 4; closes G-TVIEW1/2/3/6/7/8)

**This is a Level-A P1 blocker per the audit.** The complete WAI-ARIA `tree` widget pattern
requires the roving-tabIndex model and the full arrow-key navigation matrix — both absent in M1.

#### §FS-1.1 Roving tabIndex

- Replace the M1 model (all enabled nodes `tabIndex={0}`) with a roving tabIndex: exactly one
  node has `tabIndex={0}` at a time; all others have `tabIndex={-1}`. This resolves G-TVIEW3.
- The active node (the one at `tabIndex={0}`) is the `selected` node on mount, or the first
  root node when nothing is selected.
- The `<ul role="tree">` element itself does NOT take `tabIndex` — the active node `<div>`
  within it does.
- Tab / Shift+Tab exits the tree widget to the next focusable document element (one Tab stop
  for the whole tree).

#### §FS-1.2 ARIA attributes added in wave-3

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-label` | Root `<ul role="tree">` | Caller-supplied `aria-label` prop value, or `"Tree"` default | G-TVIEW7 |
| `aria-disabled="true"` | Disabled node `<li role="treeitem">` | `"true"` when `node.disabled` | G-TVIEW8 |
| `aria-multiselectable` | Root `<ul role="tree">` | `"true"` when `selection === 'multiple'` | audit row 3 |

#### §FS-1.3 Checkbox ARIA (wave-3 — audit row 1)

When `checkboxes === true`:

| Attribute | Element | Value |
|---|---|---|
| `role="checkbox"` | Per-node checkbox `<input>` | Native `<input type="checkbox">` provides this |
| `aria-checked` | Per-node checkbox | `"true"` / `"false"` / `"mixed"` (for `indeterminate`) |
| `aria-label` | Per-node checkbox | `"Select {node.label}"` (prevents bare "checkbox" AT announcement) |

The WAI-ARIA tree pattern does not define a standard checkbox-within-treeitem layout; use the
pattern from `APG Checkbox in Treeview` (aria-practices.org):
- The treeitem row contains two interactive elements: the checkbox and the label.
- The treeitem `<div>` retains `tabIndex` for arrow-key navigation; the checkbox is part of
  the treeitem's content but does NOT participate in roving tabIndex.
- Space on the focused treeitem row activates the checkbox (same as clicking it) when
  `checkboxes === true`; Enter activates selection.

#### §FS-1.4 Focus ring token (wave-3 — closes G-TVIEW6)

Replace hardcoded `ring-blue-500` with `ring-ring` (the fleet design-token alias). The Styling
contract owns the Tailwind class; this section documents the WCAG 2.4.7 (Level AA) requirement
that the focus ring meets minimum contrast against the node background.

#### §FS-1.5 Live region for drag-drop (wave-4 — audit row 2)

When `draggable === true`, add a visually hidden `<div role="status" aria-live="polite">` to
the TreeView root. During drag operations, update its text content:

- On drag start: `"Dragging {node.label}. Use arrow keys to select a drop position, Enter to
  drop, Escape to cancel."` (Note: pointer-only drag in wave-4; this message is aspirational
  for future keyboard drag.)
- On `onDragEnd`: `"{node.label} moved to {position} {targetNode.label}."`
- On Escape: `"Drag cancelled."`

#### §FS-1.6 Load-on-demand ARIA (wave-4 — audit row 5)

When `hasChildren === true` and `children` is not yet populated:

- The node `<li role="treeitem">` renders `aria-expanded="false"` (branch node that has not
  been expanded yet).
- While loading (after `onExpandRequest` fires and before `children` is populated), the node
  renders `aria-expanded="true"` + `aria-busy="true"` on the `<ul role="group">` child element.
- Once children are loaded, `aria-busy` is removed.
