# Sankey — Semantic Contract

- **Component:** Sankey
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Sankey.Interaction.md) · [Accessibility](./Sankey.Accessibility.md) · [Styling](./Sankey.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Sankey.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**Sankey** renders a Sankey flow diagram that visualizes the flow of quantities between nodes. Nodes represent entities (stages, categories, sources, destinations) and links represent flows between them, with link widths proportional to the flow value. It is used for energy flow analysis, budget allocation visualization, traffic source breakdowns, and any scenario where the magnitude of transfer between categories matters.

---

## 2. Props

```typescript
interface SankeyNode {
  name: string
  itemStyle?: { color?: string }    // per-node color override
}

interface SankeyLink {
  source: string    // name of the source node
  target: string    // name of the target node
  value: number     // flow magnitude (determines link width)
}

interface SankeyProps extends ChartBaseProps {
  nodes: SankeyNode[]
  links: SankeyLink[]
  orient?: 'horizontal' | 'vertical'    // flow direction; default: 'horizontal'
  nodeWidth?: number                     // width of each node rectangle in px; default: 20
  nodeGap?: number                       // vertical gap between nodes in px; default: 8
}

// Inherited from ChartBaseProps (canonical definition: Chart.Semantic.md §2.1):
// width?: string | number       default: '100%'
// height?: string | number      default: 400  (note: Sankey default is 400, not 300)
// title?: string
// subtitle?: string
// legend?: boolean | ChartLegendProps
// tooltip?: boolean | ChartTooltipProps  (default overridden to { trigger: 'item' })
// palette?: string[]
// transitions?: boolean
// className?: string
// style?: React.CSSProperties
// onSeriesClick?: (e: ChartSeriesClickEvent) => void
// onRender?: (e: ChartRenderEvent) => void
```

---

## 3. Default height

`Sankey` uses a default height of `400` (px), higher than the fleet-standard `300`. Sankey diagrams need vertical space for node stacking.

---

## 4. Tooltip trigger override

`Sankey` overrides the `tooltip` default to `{ trigger: 'item' }` when `tooltip` is falsy or not provided. This makes the tooltip fire on individual node/link hover rather than on axis cross-hair. The cast `as never` in the source suppresses TypeScript type mismatch between `ChartTooltipProps` and the raw ECharts tooltip config.

---

## 5. Link styling

Links are rendered with `lineStyle: { color: 'gradient', opacity: 0.5 }`. The gradient color makes each link fade from the source node's color to the target node's color.

---

## 6. Node identification

Nodes are identified by `name`. Links reference nodes by name via `source` and `target` string values. Node names must be unique within a `nodes` array.

---

## 7. Composition notes

`Sankey` is a leaf component. The pre-existing `SankeyChart` contracts in this directory describe a different contract shape — see whether those contracts cover this implementation or a separate `SankeyChart.tsx` component.

---

## 8. Related components

- `SankeyChart` — pre-existing contracts in this directory; may overlap with this component
- `Chart` with `type='sankey'` — passes data through `ChartSeries[]` but lacks node/link structure; not usable for real Sankey data

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SK1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
