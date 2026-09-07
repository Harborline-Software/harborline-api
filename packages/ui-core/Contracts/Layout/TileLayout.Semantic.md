# TileLayout — Semantic Contract

- **Component:** TileLayout
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TileLayout.Interaction.md) · [Accessibility](./TileLayout.Accessibility.md) · [Styling](./TileLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/TileLayout.tsx`
- **Catalog row:** #135 TileLayout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — CSS grid wrapper for draggable tiles

---

## 1. Component purpose

**TileLayout** — a CSS grid dashboard where tile items can span multiple columns and rows, and can be drag-reordered by the user. Each tile has an optional header bar and a body content area.

---

## 2. Props

```typescript
interface TileLayoutItem {
  id: string
  header?: React.ReactNode   // optional header bar
  body: React.ReactNode      // required; tile content
  col?: number               // grid column start (1-based); default: auto
  row?: number               // grid row start (1-based); default: auto
  colSpan?: number           // default: 1
  rowSpan?: number           // default: 1
  reorderable?: boolean      // default: true (drag enabled unless set to false)
  resizable?: boolean        // reserved; M1 not implemented
}

interface TileLayoutProps {
  columns?: number           // grid column count; default: 4
  rowHeight?: number | string  // row height; default: 200 (px)
  gap?: number | string      // grid gap; default: 8 (px)
  items: TileLayoutItem[]    // required
  onReorder?: (items: TileLayoutItem[]) => void
  className?: string
}
```

---

## 3. Grid model

The layout uses CSS Grid with `gridTemplateColumns: repeat(N, 1fr)`. Each tile's `gridColumn` and `gridRow` are set via inline styles. `colSpan`/`rowSpan` translate to CSS `span N`. When `col`/`row` are omitted, CSS auto-placement applies.

---

## 4. Item state

`TileLayout` maintains a local copy of `items` in state (initialized from props, synced via `useEffect`). Drag-reorder mutates the local copy and fires `onReorder`. The component is partially controlled: it owns position state but syncs from props on prop changes.

---

## 5. reorderable

`reorderable !== false` (i.e., `true` or `undefined`) enables dragging for that tile. Setting `reorderable: false` prevents that tile from being a drag source (it can still be a drop target).
