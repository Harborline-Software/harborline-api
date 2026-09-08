# Menu — Semantic Contract

- **Component:** Menu
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Menu.Interaction.md) · [Accessibility](./Menu.Accessibility.md) · [Styling](./Menu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Menu.tsx`
- **Catalog row:** #84 Menu (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 — Cohort 3 unified multi-variant
- **Foundation:** hand-rolled (no Radix primitive)
- **Absorbs:** Menubar (redirect), NavigationMenu (redirect), SideNav (passthrough shim)

---

## 1. Component purpose

**Menu** — a unified navigation component with a `variant` discriminator prop. Each variant
emits its correct WAI-ARIA pattern internally. A single import covers all four use cases:

| Variant | Use case |
|---|---|
| `"menu"` (default) | Action/navigation menu, horizontal or vertical, hover or click |
| `"menubar"` | Application-level horizontal menu bar (WAI-ARIA Menubar pattern) |
| `"navigation-menu"` | Site navigation with hover-delay panels (WAI-ARIA nav landmark + disclosure) |
| `"sidenav"` | Sidebar navigation with active state and optional groups/badges/collapsed mode |

---

## 2. Discriminated union props

```typescript
export type MenuVariant = 'menu' | 'menubar' | 'navigation-menu' | 'sidenav'

/** variant="menu" (default) */
interface MenuVariantMenuProps {
  variant?: 'menu'
  items: MenuItemWithChildren[]
  orientation?: 'horizontal' | 'vertical'   // default: 'horizontal'
  openOnClick?: boolean                       // default: false (hover-open)
  onItemClick?: (item: MenuItemWithChildren) => void
  className?: string
}

/** variant="menubar" */
interface MenuVariantMenubarProps {
  variant: 'menubar'
  items: MenubarItem[]
  className?: string
}

/** variant="navigation-menu" */
interface MenuVariantNavigationMenuProps {
  variant: 'navigation-menu'
  items: MenuItemWithChildren[]
  orientation?: 'horizontal' | 'vertical'
  delayDuration?: number          // hover open delay ms (default: 200)
  skipDelayDuration?: number      // quick-switch delay ms (default: 300)
  value?: string                  // controlled open item id
  defaultValue?: string           // uncontrolled initial open item id
  onValueChange?: (value: string) => void
  className?: string
}

/** variant="sidenav" */
interface MenuVariantSideNavProps {
  variant: 'sidenav'
  items: SideNavItemModel[] | SideNavGroup[]
  activeItemId?: string
  collapsed?: boolean             // icon-only collapsed mode
  onItemActivate?: (item: SideNavItemModel) => void
  className?: string
}

export type MenuProps =
  | MenuVariantMenuProps
  | MenuVariantMenubarProps
  | MenuVariantNavigationMenuProps
  | MenuVariantSideNavProps
```

---

## 3. Item data models

```typescript
/** Shared base */
interface MenuBaseItem {
  id: string
  label: string
  icon?: React.ReactNode
  disabled?: boolean
  href?: string
  onClick?: () => void
}

/** "menu" + "navigation-menu" variants */
interface MenuItemWithChildren extends MenuBaseItem {
  items?: MenuItemWithChildren[]
  /** navigation-menu only: rich panel content rendered below the trigger */
  panelContent?: React.ReactNode
}

/** "menubar" variant — adds item types */
type MenubarItemType = 'item' | 'checkbox' | 'radio' | 'separator' | 'sub'
interface MenubarItem extends MenuBaseItem {
  type?: MenubarItemType
  checked?: boolean
  onCheckedChange?: (checked: boolean) => void
  groupId?: string        // radio: co-exclusive items with same groupId
  items?: MenubarItem[]   // type="sub" nested items
}

/** "sidenav" variant */
interface SideNavItemModel extends MenuBaseItem {
  badge?: React.ReactNode
  children?: SideNavItemModel[]
}

interface SideNavGroup {
  id: string
  label?: string
  items: SideNavItemModel[]
}
```

---

## 4. State model

- `"menu"`: each item manages its own `open` boolean (uncontrolled)
- `"menubar"`: single `openMenu: string | null` — at most one top-level menu open at a time
- `"navigation-menu"`: single `value: string` — at most one panel open; controlled or uncontrolled
- `"sidenav"`: `activeItemId` is controlled; expand/collapse per nested item is local uncontrolled state

---

## 5. Events

| Variant | Event | Trigger | Payload |
|---|---|---|---|
| `"menu"` | `onItemClick` | Leaf item clicked | `item: MenuItemWithChildren` |
| `"menubar"` | `item.onClick` | Item activated (per-item) | `void` |
| `"menubar"` | `item.onCheckedChange` | Checkbox/radio item toggled | `boolean` |
| `"navigation-menu"` | `onValueChange` | Panel open/close | `string` (item id or `''`) |
| `"navigation-menu"` | `item.onClick` | Link item clicked (per-item) | `void` |
| `"sidenav"` | `onItemActivate` | Any item clicked (not disabled) | `item: SideNavItemModel` |

---

## 6. Item rendering

- Leaf items with `href` render as `<a href="...">` (links)
- Leaf items without `href` render as `<button type="button">`
- Disabled state: `disabled` attribute on buttons, `aria-disabled` on both links and buttons

---

## 7. Submenu open mode (variant="menu")

| `openOnClick` | Behavior |
|---|---|
| `false` (default) | Submenu opens on `mouseenter`, closes on `mouseleave` |
| `true` | Submenu opens/closes on click (toggle) |

---

## 8. Backward-compatibility shims

- `SideNav` re-exported as `function SideNav(props) { return <Menu variant="sidenav" {...props} /> }`
- `Menubar` composable sub-exports removed (hard delete — zero consumers); data-driven items only
- `NavigationMenu` composable sub-exports removed (hard delete — zero consumers)
