# SankeyChart — Semantic Contract

- **Component:** SankeyChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SankeyChart.Interaction.md) · [Accessibility](./SankeyChart.Accessibility.md) · [Styling](./SankeyChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A23 SankeyChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; D3-Sankey / Recharts Sankey baseline)

---

## 1. Component purpose

**SankeyChart** — a flow diagram where node widths and link widths are proportional to flow quantity. Nodes are arranged in vertical columns; curved links (Bézier paths) connect them. Used for energy flows, user journey funnels, budget allocation.

---

## 2. Data model

```typescript
interface SankeyNode {
  id: string
  name: string
  color?: string
}

interface SankeyLink {
  source: string
  target: string
  value: number
  color?: string
}

interface SankeyChartProps {
  nodes: SankeyNode[]
  links: SankeyLink[]
  nodePadding?: number
  nodeWidth?: number
  iterations?: number
  width?: number | string
  height?: number
  tooltip?: boolean
  legend?: boolean
  className?: string
}
```

---

## 3. Layout algorithm

D3-Sankey iterative layout: nodes assigned to columns by longest-path algorithm. `iterations` controls relaxation passes (default 32). `nodePadding` sets vertical gap between nodes in the same column (default 8px). `nodeWidth` sets the horizontal bar thickness (default 24px).

---

## 4. Link rendering

Cubic Bézier paths between source node right-edge and target node left-edge. Link `y` position offsets accumulate as flow is stacked. Overlapping links use opacity to show depth.

---

## 5. Color assignment

Nodes use `hsl(var(--chart-N))` cycling when `node.color` is not specified. Links default to source node color at reduced opacity.
