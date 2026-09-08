# BottomNavigation — Semantic Contract

- **Component:** BottomNavigation
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./BottomNavigation.Interaction.md) · [Accessibility](./BottomNavigation.Accessibility.md) · [Styling](./BottomNavigation.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/BottomNavigation.tsx`
- **Catalog row:** #12 BottomNavigation (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<nav>` bottom bar

---

## 1. Component purpose

**BottomNavigation** — a fixed bottom navigation bar for mobile interfaces. Renders a row of tab-like buttons, each with an icon, optional text label, and optional badge counter. Supports single-selection by index.

---

## 2. Props

```typescript
interface BottomNavigationItem {
  text?: string              // optional label below icon
  icon?: React.ReactNode     // optional icon
  badge?: number | string    // optional badge overlay on icon
  disabled?: boolean         // default: false
}

interface BottomNavigationProps {
  items: BottomNavigationItem[]   // required; navigation items
  value?: number                  // controlled; selected index
  defaultValue?: number           // default: 0
  onValueChange?: (index: number) => void
  fill?: 'flat' | 'solid'        // default: 'flat'
  shadow?: boolean                // default: true
  className?: string
}
```

---

## 3. Selection model

Selection is by **index** (zero-based integer), not by item value. When `selection === i`, that item is considered selected. `onValueChange` receives the index of the newly-selected item.

---

## 4. Badge

When `item.badge` is provided, a numeric/string counter badge is rendered absolutely over the icon's top-right corner. Badge uses destructive color to draw attention.

---

## 5. Fill modes

`fill='flat'` (default): white/background surface with foreground text — standard bottom nav.
`fill='solid'`: primary brand color surface — inverted text; all items use `text-primary-foreground` with reduced opacity for unselected items.
