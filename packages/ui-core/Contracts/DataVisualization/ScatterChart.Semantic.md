# ScatterChart — Semantic Contract

- **Component:** ScatterChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./ScatterChart.Interaction.md) · [Accessibility](./ScatterChart.Accessibility.md) · [Styling](./ScatterChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts ScatterChart baseline)
- **Catalog row:** #A33 ScatterChart (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts baseline)

---

## 1. Component purpose

**ScatterChart** — renders individual data points as symbols (dots, crosses, etc.) on a Cartesian XY plane. Used for correlation analysis and distribution visualization. Distinct from BubbleChart (no size encoding).

---

## 2. Props (planned)

```typescript
interface ScatterPoint {
  x: number
  y: number
  label?: string
  [key: string]: unknown
}

interface ScatterSeries {
  data: ScatterPoint[]
  name?: string
  color?: string
  symbol?: 'circle' | 'cross' | 'diamond' | 'square' | 'star' | 'triangle'
  symbolSize?: number    // default: 6
}

interface ScatterChartProps {
  series: ScatterSeries[]
  width?: number | string
  height?: number
  xAxisLabel?: string
  yAxisLabel?: string
  xDomain?: [number | 'auto', number | 'auto']
  yDomain?: [number | 'auto', number | 'auto']
  legend?: boolean
  tooltip?: boolean
  grid?: boolean
  referenceLine?: { x?: number; y?: number; label?: string }[]
  className?: string
}
```

---

## 3. Reference lines

Optional horizontal or vertical reference lines (e.g. average, threshold). Rendered as dashed lines across the chart area.

---

## 4. Multi-series

Multiple series with different `symbol` shapes help differentiate groups in colorblind-safe mode.

---

## 5. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
