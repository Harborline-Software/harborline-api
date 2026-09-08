# AreaChart — Semantic Contract

- **Component:** AreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./AreaChart.Interaction.md) · [Accessibility](./AreaChart.Accessibility.md) · [Styling](./AreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts AreaChart / Telerik Chart baseline)
- **Catalog row:** #A1 AreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**AreaChart** — renders one or more filled area series on a Cartesian grid with X and Y axes. Used for visualizing continuous trends over time (e.g. revenue, usage, sensor readings). Supports stacked areas.

---

## 2. Props (planned)

```typescript
interface ChartDataPoint {
  [key: string]: string | number
}

interface AreaSeries {
  dataKey: string
  name?: string
  color?: string
  fillOpacity?: number           // default: 0.3
  strokeWidth?: number           // default: 2
  smooth?: boolean               // Catmull-Rom curve; default: false
  stacked?: boolean              // stack this series on previous; default: false
  dot?: boolean                  // show data-point markers; default: false
}

interface AreaChartProps {
  data: ChartDataPoint[]
  series: AreaSeries[]
  categoryKey: string            // key for x-axis category labels (matches BarChart / LineChart API)
  width?: number | string        // default: '100%'
  height?: number                // default: 300
  xAxisLabel?: string
  yAxisLabel?: string
  yDomain?: [number | 'auto', number | 'auto']  // default: ['auto', 'auto']
  legend?: boolean               // default: true
  tooltip?: boolean              // default: true
  grid?: boolean                 // show horizontal grid lines; default: true
  className?: string
}
```

---

## 3. Multi-series

Multiple `series` entries render overlapping or stacked area fills. When `stacked=true` on a series, its values are added on top of the previous series baseline (standard stacked area chart behavior).

---

## 4. Axes

X axis: categorical or time-based string labels. Y axis: numeric, auto-scaled to data range unless `yDomain` is set.

---

## 5. Legend

When `legend=true`, a legend renders below the chart mapping each series name to its color swatch.

---

## 6. Responsive sizing

When `width='100%'`, the chart is wrapped in a `ResponsiveContainer` that fills its parent.

---

## 7. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
