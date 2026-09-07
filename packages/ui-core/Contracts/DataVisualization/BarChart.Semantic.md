# BarChart — Semantic Contract

- **Component:** BarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./BarChart.Interaction.md) · [Accessibility](./BarChart.Accessibility.md) · [Styling](./BarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart / Telerik Chart baseline)
- **Catalog row:** #A2 BarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**BarChart** — renders horizontal bars for comparing values across categories. Distinct from ColumnChart (which is vertical). Used for ranked comparisons, leaderboards, and categorical breakdowns.

---

## 2. Props (planned)

```typescript
interface BarSeries {
  dataKey: string
  name?: string
  color?: string
  stacked?: boolean          // default: false
  labelPosition?: 'inside' | 'outside' | 'none'  // default: 'none'
}

interface BarChartProps {
  data: ChartDataPoint[]     // see [AreaChart.Semantic.md](./AreaChart.Semantic.md) for ChartDataPoint
  series: BarSeries[]
  categoryKey: string        // key for category labels (y-axis in horizontal bar)
  width?: number | string    // default: '100%'
  height?: number            // default: 300
  layout?: 'horizontal' | 'vertical'  // default: 'horizontal' (bars run left→right)
  barSize?: number           // bar thickness in px; default: auto
  barGap?: number            // gap between grouped bars; default: 4
  legend?: boolean           // default: true
  tooltip?: boolean          // default: true
  grid?: boolean             // default: true
  className?: string
}
```

---

## 3. Layout

`layout='horizontal'`: bars extend left-to-right; categories on y-axis. This is the canonical "bar chart" orientation.

`layout='vertical'`: bars extend bottom-to-top; categories on x-axis. This is what ColumnChart renders — BarChart with `layout='vertical'` is equivalent.

---

## 4. Grouped vs stacked

Multiple `series` with `stacked=false` renders grouped bars side by side. With `stacked=true`, bars stack (positive values upward, negative downward for standard stacking).

---

## 5. Bar labels

When `labelPosition` is `'inside'` or `'outside'`, the bar's value is rendered as a text label at the bar tip.

---

## 6. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
