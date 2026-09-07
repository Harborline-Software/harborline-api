# Sankey — Styling Contract

- **Component:** Sankey
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sankey.Semantic.md) · [Interaction](./Sankey.Interaction.md) · [Accessibility](./Sankey.Accessibility.md) · [Styling](./Sankey.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Sankey.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Plain `<div>` receiving `className` (via `cn()`) and inline `style` combining `{ width, height }` with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `400` (px — higher than the fleet standard 300).

---

## 2. Node appearance

- Width: `nodeWidth` prop (default `20` px)
- Vertical gap between nodes: `nodeGap` prop (default `8` px)
- Node color: per-node `itemStyle.color` override; otherwise ECharts default or `palette`-derived

---

## 3. Link appearance

```
lineStyle: { color: 'gradient', opacity: 0.5 }
```

Links render as gradient bands fading from the source node color to the target node color at 50% opacity. Link width is proportional to the `value` of the `SankeyLink`.

---

## 4. Orientation

`orient: 'horizontal'` (default) — nodes arranged in columns left to right; links flow left to right.
`orient: 'vertical'` — nodes arranged in rows top to bottom; links flow top to bottom.

---

## 5. Tooltip

ECharts item-trigger tooltip; styled by `buildBaseOption` ECharts theme.

---

## 6. Design tokens

No direct design-token references in the component source. Token consumption deferred to `buildBaseOption` / ECharts theme layer. Node colors can be mapped to design tokens by passing explicit `itemStyle.color` values resolved from CSS variables at the call site.
