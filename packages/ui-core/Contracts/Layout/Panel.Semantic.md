# Panel — Semantic Contract

- **Component:** Panel
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Panel.Interaction.md) · [Accessibility](./Panel.Accessibility.md) · [Styling](./Panel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Panel.tsx`
- **Catalog row:** #95 Panel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `mode="accordion"` + `resizable`
- **Foundation:** none — hand-rolled `<div>` panel container

---

## 1. Component purpose

**Panel** — a visual content container that dispatches on the `mode` prop:

- `mode="static"` (default): visual grouping container with optional header, footer, variant, padding, and optional `resizable` capability.
- `mode="accordion"`: data-driven multi-item accordion list. Absorbs the deprecated **PanelBar** component. Each item can expand to reveal child items or content.

Boundary with Splitter: one container resizing itself → Panel `resizable`. N panes sharing space with handles between them → Splitter (unchanged).

---

## 2. Props — static mode

```typescript
interface PanelStaticProps extends React.HTMLAttributes<HTMLDivElement> {
  mode?: 'static'                                             // default
  variant?: 'default' | 'filled' | 'bordered' | 'elevated'  // default: 'default'
  padding?: 'none' | 'sm' | 'md' | 'lg'                     // default: 'md'
  header?: React.ReactNode
  footer?: React.ReactNode
  /**
   * When true: shows a corner resize handle with default options.
   * When an object: full control over directions, min/max, controlled size,
   * and callbacks. Not available in mode="accordion".
   */
  resizable?: boolean | PanelResizeOptions
}

interface PanelResizeOptions {
  directions?: Array<'right' | 'bottom' | 'corner'>  // default: ['corner']
  minWidth?: number
  minHeight?: number
  maxWidth?: number
  maxHeight?: number
  size?: { width?: number; height?: number }          // controlled size
  onResize?: (size: { width: number; height: number }) => void
  onResizeEnd?: (size: { width: number; height: number }) => void
}
```

All `React.HTMLAttributes<HTMLDivElement>` (including `className`, `aria-*`, `id`, etc.) are spread onto the root `<div>`.

---

## 3. Props — accordion mode (absorbs PanelBar)

```typescript
interface PanelAccordionProps {
  mode: 'accordion'
  items: PanelBarItem[]
  expandMode?: 'single' | 'multiple'        // default: 'single'
  onSelect?: (item: PanelBarItem) => void   // fires when a leaf item is clicked
  onExpand?: (item: PanelBarItem, expanded: boolean) => void
  className?: string
  // resizable is NOT available in accordion mode (discriminated union)
}

interface PanelBarItem {
  id: string
  text: string
  icon?: React.ReactNode
  disabled?: boolean
  expanded?: boolean   // seeds initial expanded state (uncontrolled)
  items?: PanelBarItem[]
  content?: React.ReactNode
}
```

---

## 4. Static mode — header / footer slots

When `header` is provided, renders above the body with a `border-b`. When `padding='none'`, the header uses `'sm'` padding as a minimum.

When `footer` is provided, renders below the body with a `border-t` and `bg-gray-50`. Same padding fallback.

---

## 5. Resizable capability (static mode only)

Panel can self-resize. This is distinct from Splitter, which manages N panes with shared space.

- `resizable={true}`: corner handle only, default options.
- `resizable={{ directions: [...], onResize, onResizeEnd, ... }}`: full control.
- Directions: `'right'` (horizontal handle), `'bottom'` (vertical handle), `'corner'` (diagonal handle).
- When `resizable.size` is provided: controlled mode — Panel uses the provided dimensions.
- When `resizable.size` is absent: uncontrolled mode — Panel tracks size internally from initial DOM dimensions.

---

## 6. Accordion mode — expand/collapse

- Items with `items` children are toggle nodes. Clicking toggles expanded state.
- Items without `items` children are leaf nodes. Clicking fires `onSelect`.
- `expandMode="single"` (default): expanding one item collapses all others.
- `expandMode="multiple"`: all items can be expanded simultaneously.
- `item.expanded=true` in data seeds the initial expanded state.

---

## 7. PanelBar migration

`PanelBar` is a passthrough shim over `Panel mode="accordion"`. See [PanelBar.Semantic.md](./PanelBar.Semantic.md).
