# DockManager — Semantic Contract

- **Component:** DockManager
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DockManager.Interaction.md) · [Accessibility](./DockManager.Accessibility.md) · [Styling](./DockManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DockManager.tsx`
- **Catalog row:** #44 DockManager (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled dockable panel manager

---

## 1. Component purpose

**DockManager** — an IDE-style dockable panel layout with five zones (top, left, center, right, bottom). Each zone shows a tabbed panel group. Panels can be closed; active tab per zone is tracked separately.

---

## 2. Props

```typescript
interface DockPanel {
  id: string
  title: string
  content: React.ReactNode
  closable?: boolean            // default: true (closable unless explicitly false)
  dockZone?: 'left' | 'right' | 'top' | 'bottom' | 'center'  // default: 'center'
  size?: number                 // reserved; M1 uses fixed CSS sizing
}

interface DockManagerProps {
  panels: DockPanel[]
  onPanelClose?: (id: string) => void
  onLayoutChange?: (panels: DockPanel[]) => void  // fires on every re-dock (§5)
  className?: string
}

// `DockZoneId` is exported for typing `dockZone` / layout state.
type DockZoneId = 'left' | 'right' | 'top' | 'bottom' | 'center'
```

---

## 3. Zone layout

Panels are grouped by `dockZone`. A zone is only rendered when it has at least one panel. Layout:
```
┌──────────────────────────────┐
│          top (h-1/4)         │
├────────┬──────────┬──────────┤
│ left   │  center  │  right   │
│ (w-56) │ (flex-1) │  (w-56)  │
├──────────────────────────────┤
│         bottom (h-1/4)       │
└──────────────────────────────┘
```

---

## 4. Active tab tracking

Each zone tracks its active panel ID in local state (`activeIds`). Initialized to the first panel in each zone. Clicking a tab activates that panel. Panel close removes the panel from the list (tab is removed from the zone).

---

## 5. onLayoutChange

`onLayoutChange(panels)` fires whenever a panel is **re-docked** — by pointer
drag-between-zones or the keyboard "Move to…" menu — with the full, re-ordered
panel list reflecting the new arrangement (each panel's `dockZone` updated; the
moved panel ordered last in its destination zone). It drives controlled usage:
when the parent re-feeds `panels` in response, the prop-sync effect reflects it.
Panel **close** fires `onPanelClose(id)` only (not `onLayoutChange`).
