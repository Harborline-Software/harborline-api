# ContextMenu — Semantic Contract

- **Component:** ContextMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ContextMenu.Interaction.md) · [Accessibility](./ContextMenu.Accessibility.md) · [Styling](./ContextMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ContextMenu.tsx`
- **Catalog row:** #34 ContextMenu (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled context menu (no Radix ContextMenu primitive)

---

## 1. Component purpose

**ContextMenu** — a right-click context menu wrapper. Wraps its children and intercepts `onContextMenu` events to show a positioned menu at the cursor. Items are organized into groups separated by dividers.

---

## 2. Props

```typescript
interface ContextMenuItem {
  id: string
  label: React.ReactNode
  icon?: React.ReactNode
  disabled?: boolean
  danger?: boolean          // renders in destructive color
  onSelect: () => void
}

interface ContextMenuGroup {
  items: ContextMenuItem[]
}

interface ContextMenuProps {
  groups: ContextMenuGroup[]   // required; grouped item list
  children: React.ReactNode    // required; the right-clickable content
  className?: string
}
```

---

## 3. Trigger mechanism

`ContextMenu` wraps `children` in a `<div onContextMenu={...}>`. The native right-click context menu is suppressed via `e.preventDefault()`. The custom menu appears at `clientX/clientY` (fixed positioning).

---

## 4. Groups and separators

Multiple groups are separated by a horizontal divider (`role="separator"`). Items within each group are rendered consecutively with no divider between them.

---

## 5. Danger items

Items with `danger: true` render in red/destructive styling with a matching hover background.

---

## 6. Position model

Menu is positioned `fixed` at the exact cursor position. No viewport-edge collision detection in M1 — if the cursor is near the right or bottom edge, the menu may clip outside the viewport.

---

## 7. Menu callback family — naming rationale

The menu family uses divergent callback names for historical and semantic reasons.
No single family-wide name was chosen because the payload and semantics differ:

| Component | Callback | Payload | Rationale |
|---|---|---|---|
| Menu | `onItemClick` | `MenuItem` | Carries full item object — host needs to know which item |
| Menubar | `onSelect` | `void` | Items are self-describing; host typically uses `item.onClick` per-item |
| ContextMenu | `onSelect` | `void` | Same as Menubar — context actions are self-contained |
| ActionMenu | item-level `onClick` | `void` | Per-item direct wiring; no top-level aggregator |
| NavigationMenu | `onValueChange` (state) + `onSelect` (action) | `string` / `Event` | Radix-based; `onValueChange` tracks open panel; `onSelect` fires on link activation |

**PL3-6 disposition:** Divergence is intentional and documented here. A future M2 alignment pass may unify to `onSelect(item)` across command menus — tracked but not blocking.
