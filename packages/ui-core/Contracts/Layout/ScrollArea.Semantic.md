# ScrollArea — Semantic Contract

- **Component:** ScrollArea
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ScrollArea.Interaction.md) · [Accessibility](./ScrollArea.Accessibility.md) · [Styling](./ScrollArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Radix @radix-ui/react-scroll-area)
- **Catalog row:** #A16 ScrollArea (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix ScrollArea baseline)

---

## 1. Component purpose

**ScrollArea** — a scrollable container with a custom-styled scrollbar that replaces the browser's native scrollbar. Provides consistent cross-browser scrollbar appearance while preserving native scroll behavior and accessibility.

---

## 2. Compound component API (planned)

```typescript
interface ScrollAreaProps extends React.HTMLAttributes<HTMLDivElement> {
  type?: 'auto' | 'always' | 'scroll' | 'hover'  // scrollbar visibility; default: 'hover'
  scrollHideDelay?: number                         // ms to hide after scroll stops; default: 600
  dir?: 'ltr' | 'rtl'
  className?: string
  children?: React.ReactNode
}

interface ScrollAreaScrollbarProps {
  orientation?: 'vertical' | 'horizontal'  // default: 'vertical'
  className?: string
}

interface ScrollAreaThumbProps {
  className?: string
}

interface ScrollAreaViewportProps extends React.HTMLAttributes<HTMLDivElement> {}

interface ScrollAreaCornerProps extends React.HTMLAttributes<HTMLDivElement> {}
```

---

## 3. Scrollbar visibility types

- `auto`: shows when content overflows
- `always`: always visible
- `scroll`: visible while scrolling
- `hover`: visible on hover (default)

---

## 4. Usage pattern

```tsx
<ScrollArea className="h-72 w-48">
  <ScrollAreaViewport>{/* content */}</ScrollAreaViewport>
  <ScrollAreaScrollbar orientation="vertical">
    <ScrollAreaThumb />
  </ScrollAreaScrollbar>
</ScrollArea>
```
