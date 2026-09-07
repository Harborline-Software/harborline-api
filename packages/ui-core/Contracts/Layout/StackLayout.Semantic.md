# StackLayout — Semantic Contract

- **Component:** StackLayout
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./StackLayout.Interaction.md) · [Accessibility](./StackLayout.Accessibility.md) · [Styling](./StackLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/StackLayout.tsx`
- **Catalog row:** #127 StackLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — CSS flexbox wrapper

---

## 1. Component purpose

**StackLayout** — a flex layout primitive for stacking children horizontally or vertically with configurable gap, alignment, and wrapping.

---

## 2. Props

```typescript
interface StackLayoutProps {
  orientation?: 'horizontal' | 'vertical'   // default: 'vertical'
  gap?: string | number                      // default: '0.5rem' — number → px
  align?: React.CSSProperties['alignItems']
  justify?: React.CSSProperties['justifyContent']
  wrap?: boolean                             // default: false
  children?: React.ReactNode
  className?: string
}
```

---

## 3. Renders

Single `<div>` with `flex flex-col` (vertical) or `flex flex-row` (horizontal), plus inline styles for gap/align/justify/wrap.

---

## 4. Gap conversion

`gap` as number → `${gap}px`. As string → passthrough to CSS `gap` property.
