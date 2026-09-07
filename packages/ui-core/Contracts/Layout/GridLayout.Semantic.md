# GridLayout — Semantic Contract

- **Component:** GridLayout
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./GridLayout.Interaction.md) · [Accessibility](./GridLayout.Accessibility.md) · [Styling](./GridLayout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/GridLayout.tsx`
- **Catalog row:** #67 GridLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — CSS grid wrapper

---

## 1. Component purpose

**GridLayout** — a compound layout primitive wrapping CSS grid. Exposes grid template columns/rows, gap, align, and justify as props. `GridLayout.Item` provides individual cell placement via CSS `grid-row` and `grid-column`.

---

## 2. Props

```typescript
// GridLayout (root)
interface GridLayoutProps {
  columns?: string | number  // default: '1fr' — number → repeat(N, 1fr)
  rows?: string | number     // number → repeat(N, 1fr); string → passthrough
  gap?: string | number      // default: '1rem' — number → px string
  align?: React.CSSProperties['alignItems']
  justify?: React.CSSProperties['justifyItems']
  children?: React.ReactNode
  className?: string
}

// GridLayout.Item
interface GridLayoutItemProps {
  row?: string | number      // CSS grid-row value
  col?: string | number      // CSS grid-column value
  rowSpan?: number           // grid-row: {row} / span {rowSpan}
  colSpan?: number           // grid-column: {col} / span {colSpan}
  children?: React.ReactNode
  className?: string
}
```

---

## 3. Compound component export

`GridLayout` is exported as `Object.assign(GridLayoutRoot, { Item: GridLayoutItem })`. Usage: `<GridLayout>` + `<GridLayout.Item>`.

---

## 4. Numeric shorthand conversions

- `columns={3}` → `gridTemplateColumns: 'repeat(3, 1fr)'`
- `rows={2}` → `gridTemplateRows: 'repeat(2, 1fr)'`
- `gap={16}` → `gap: '16px'`

---

## 5. Item placement

When `rowSpan` is set: `gridRow: "${row ?? 'auto'} / span ${rowSpan}"`

When `row` is set but no `rowSpan`: `gridRow: String(row)`

Same pattern for `colSpan`/`col`.
