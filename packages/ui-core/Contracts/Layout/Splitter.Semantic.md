# Splitter — Semantic Contract

- **Component:** Splitter
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Splitter.Interaction.md) · [Accessibility](./Splitter.Accessibility.md) · [Styling](./Splitter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Splitter.tsx`
- **Catalog row:** #125 Splitter (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled resizable panel splitter

---

## 1. Component purpose

**Splitter** — a resizable split container. Renders two or more panes separated by draggable dividers. Orientation can be horizontal (side-by-side) or vertical (stacked).

---

## 2. Props

```typescript
interface SplitterPane {
  content: React.ReactNode
  size?: string | number    // initial size: px or '%'; default: equal share
  min?: string | number     // minimum size (reserved; M1 enforces 10px hardcoded min)
  max?: string | number     // maximum size (reserved; M1 not enforced)
  collapsible?: boolean     // reserved; M1 renders pane.collapsed visually
  collapsed?: boolean       // if true, pane content is not rendered
}

interface SplitterProps {
  panes: SplitterPane[]               // required; minimum 2
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  onPaneResize?: (panes: SplitterPane[]) => void
  className?: string
}
```

---

## 3. Size model

Initial sizes are derived from `pane.size`:
- `number` → pixels
- `'N%'` → percentage of container dimension
- `'Npx'` → pixels
- omitted → equal share (`total / paneCount`)

Sizes are stored as pixel values in component state.

---

## 4. Minimum pane size

The hard minimum in M1 is 10px per pane (enforced in the drag handler via `Math.max(10, ...)`). `SplitterPane.min` is accepted but not used in M1.

---

## 5. Collapsed panes

When `pane.collapsed=true`, the pane's `content` is not rendered (null), but the pane's size slot still occupies space. This is a visual-only collapse with no animation.
