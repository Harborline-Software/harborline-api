# LineChart — Semantic Contract

- **Component:** LineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./LineChart.Interaction.md) · [Accessibility](./LineChart.Accessibility.md) · [Styling](./LineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts LineChart / Telerik Chart baseline)
- **Catalog row:** #A3 LineChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**LineChart** — renders one or more line series on a Cartesian grid. Used for time-series data, performance trends, and comparative metrics. Distinct from AreaChart (no fill beneath the line).

---

## 2. Props (planned)

```typescript
interface LineSeries {
  dataKey: string
  name?: string
  color?: string
  strokeWidth?: number       // default: 2
  smooth?: boolean           // default: false
  dot?: boolean              // show markers; default: false
  dotSize?: number           // default: 4
  strokeDasharray?: string   // e.g. '5 5' for dashed line
}

interface LineChartProps {
  data: ChartDataPoint[]
  series: LineSeries[]
  categoryKey: string        // key for x-axis category labels (matches BarChart / AreaChart API)
  width?: number | string    // default: '100%'
  height?: number            // default: 300
  xAxisLabel?: string
  yAxisLabel?: string
  yDomain?: [number | 'auto', number | 'auto']
  legend?: boolean           // default: true
  tooltip?: boolean          // default: true
  grid?: boolean             // default: true
  connectNulls?: boolean     // connect line across null/missing values; default: false
  className?: string
}
```

---

## 3. Null handling

When `connectNulls=false` (default), a gap is rendered in the line at null/undefined data points. When `connectNulls=true`, the line bridges across missing values.

---

## 4. Axes, legend, tooltip

Same semantics as AreaChart — see [AreaChart.Semantic.md §4-6](./AreaChart.Semantic.md).

---

## 5. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
