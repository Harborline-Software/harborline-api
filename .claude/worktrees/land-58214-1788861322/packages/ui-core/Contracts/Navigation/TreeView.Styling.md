# TreeView — Styling Contract

- **Component:** TreeView
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeView.Semantic.md) · [Interaction](./TreeView.Interaction.md) · [Accessibility](./TreeView.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/TreeView.tsx`
- **Catalog row:** #142 TreeView (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root list

`select-none` + `className` passthrough. No other default classes.

---

## 2. Node row

Base: `flex cursor-pointer items-center gap-2 rounded-md py-1.5 pr-2 text-sm transition-colors select-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`

| State | Classes |
|---|---|
| Default | `text-gray-700 hover:bg-gray-100` |
| Selected | `bg-blue-50 text-blue-700 font-medium` |
| Disabled | `pointer-events-none opacity-50` |

> **M1 note:** Selected and default states use hardcoded Tailwind colors (`blue-50`, `blue-700`, `gray-700`, `gray-100`) rather than design tokens. Replace with `bg-accent text-accent-foreground` / `hover:bg-accent` in M2+.

---

## 3. Indentation

Inline style: `paddingLeft: ${level * 1.25 + 0.5}rem`

| Level | Left padding |
|---|---|
| 0 (root) | `0.5rem` |
| 1 | `1.75rem` |
| 2 | `3rem` |
| N | `(N × 1.25 + 0.5)rem` |

---

## 4. Expand/collapse toggle button

`shrink-0 text-gray-400 hover:text-gray-600`

Chevron SVG: `h-3.5 w-3.5 transition-transform` + `rotate-90` when expanded.

---

## 5. Leaf spacer

`h-3.5 w-3.5 shrink-0` (aria-hidden) — aligns leaf labels with branch labels that have a chevron.

---

## 6. Icon slot

`shrink-0` wrapper span. No size constraints — icon determines its own size.

---

## 7. Label

`truncate` — long labels are truncated with ellipsis.

---

## 8. Children group list

`role="group"` — no layout classes. Children are positioned by the parent `<li>` containing block.

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeView
styling surface gaps (audit rows 1, 2, 3). FR-3 rulings apply (see
`_shared/design/polish/family-rulings-2026-06-11.md`).

Audit rows 5 (load-on-demand) and 6 (itemRender) have no distinct styling surface: the
wrapping node row `<div>` structure is unchanged; `itemRender` replaces label+icon content
inside the existing row; load-on-demand uses `aria-busy` on the existing `<ul role="group">`
element. Both are fully specced by the Semantic and Accessibility contracts.

---

### §FS-1 Wave — design token migration + checkbox + multi-select visuals

#### §FS-1.1 Token migration for node row (wave-3; closes M1 hardcoded-color note)

Replace all hardcoded Tailwind palette values in §2 with fleet design tokens:

| M1 class | Wave-3 replacement | Token meaning |
|---|---|---|
| `text-gray-700` | `text-foreground` | Default text |
| `hover:bg-gray-100` | `hover:bg-accent` | Hover wash |
| `bg-blue-50 text-blue-700 font-medium` | `bg-accent text-accent-foreground font-medium` | Selected state |
| `focus-visible:ring-blue-500` | `focus-visible:ring-ring` | Focus ring (Accessibility §FS-1.4) |
| expand toggle `text-gray-400 hover:text-gray-600` | `text-muted-foreground hover:text-foreground` | Toggle icon |

FR-3 `size` axis: node row height is `md` by default (`py-1.5`). When `size='sm'` is provided
on `TreeViewProps`, reduce to `py-1`; `size='lg'` increases to `py-2`. (Inherits FR-3
vocabulary; `TreeViewProps.size?: 'sm' | 'md' | 'lg'`.)

**wave-3**

---

#### §FS-1.2 Checkbox styling (wave-3 — audit row 1)

When `checkboxes === true`, each node row prepends:

```
<input type="checkbox" class="h-4 w-4 shrink-0 rounded border border-input accent-primary cursor-pointer" />
```

- Margins: `mr-1.5` between checkbox and expand toggle (or label when expand is absent).
- `disabled` state: `opacity-50 cursor-not-allowed` (same disabled treatment as the row).
- `indeterminate` state: rendered via the browser's native indeterminate glyph; no additional
  Tailwind class needed beyond the base above.

**wave-3**

---

#### §FS-1.3 Multi-select: selected-set visual (wave-3 — audit row 3)

When `selection === 'multiple'` and a node is in `selectedIds`:

- Apply the same `bg-accent text-accent-foreground font-medium` classes as the single-select
  selected state.
- "Anchor" node (the last directly-clicked node for Shift+click range selection): no distinct
  visual treatment in wave-3.

**wave-3**

---

#### §FS-1.4 Drag clue + drop indicator (wave-4 — audit row 2)

**Drag clue element** (portal-rendered at the cursor):

```
TreeViewDragClue: absolute pointer-events-none z-50 rounded-md border border-border
  bg-background px-2 py-1 text-sm shadow-md opacity-80 flex items-center gap-2
```

Contains the node's icon (if any) and label text.

**Drop indicator lines (before/after):**

```
position: absolute, left: 0, right: 0
height: 2px, background-color: hsl(var(--primary)), border-radius: 1px
z-index: 50
```

**Drop indicator inside (target-row highlight):**

```
outline: 2px solid hsl(var(--primary)), outline-offset: -2px
border-radius: var(--radius-md)
```

These are applied to the row `<div>` as an inline style during hover; removed on drag end.

**wave-4**
