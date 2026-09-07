# ActionMenu — Semantic Contract

- **Component:** ActionMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ActionMenu.Interaction.md) · [Accessibility](./ActionMenu.Accessibility.md) · [Styling](./ActionMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/ActionMenu.tsx`
- **Catalog row:** #A25 ActionMenu (`app-priority: medium`, `library-scope: v1`) — Harborline-native action menu
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled dropdown action menu

---

## 1. Component purpose

**ActionMenu** — a disclosure dropdown that shows a list of actions triggered by a button. Supports standard items and destructive items. Uses a 3-dot button as default trigger. Items are defined declaratively via the `items` prop.

---

## 2. Props

```typescript
interface ActionMenuItem {
  label: string
  onClick: () => void
  variant?: 'default' | 'destructive'  // default: 'default'
  disabled?: boolean
  icon?: React.ReactNode
}

interface ActionMenuSeparator {
  type: 'separator'
}

interface ActionMenuProps {
  items: (ActionMenuItem | ActionMenuSeparator)[]
  trigger?: React.ReactNode   // default: 3-dot button with aria-label="More actions"
  align?: 'left' | 'right'   // default: 'right'
  className?: string
}
```

---

## 3. Default trigger

When `trigger` is omitted, renders a `<button>` with:
- `aria-label="More actions"`
- SVG 3-dot horizontal icon (`aria-hidden`)
- `aria-haspopup="menu"` and `aria-expanded` set on the container `<div>`

---

## 4. Item variants

`default`: standard action. `destructive`: action with destructive visual styling (red text).

---

## 5. Separator

`{ type: 'separator' }` items render as `<hr>` dividers between item groups.

---

## 6. Menu callback family — naming rationale

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
