# ScatterLineChart — Semantic Contract

- **Component:** ScatterLineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ScatterLineChart.Interaction.md) · [Accessibility](./ScatterLineChart.Accessibility.md) · [Styling](./ScatterLineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ScatterLineChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**ScatterLineChart** renders one or more scatter datasets as connected line series on a dual-numeric (value-value) Cartesian grid. Unlike `ScatterChart` which renders isolated points, `ScatterLineChart` sorts each series by X value and connects the points with lines, making trends and relationships between two continuous variables legible. It is used for regression lines, trend visualizations, correlation plots, and scientific data where connecting the scatter points communicates continuity.

---

## 2. Props

```typescript
// ScatterDataItem is imported from ScatterChart:
interface ScatterDataItem {
  x: number
  y: number
}

interface ScatterLineChartProps extends ChartBaseProps {
  series: Array<{
    name: string
    data: ScatterDataItem[]
    color?: string
  }>
  smooth?: boolean         // Catmull-Rom smooth curve on the connecting line; default: false
  xAxisTitle?: string      // label on the X axis
  yAxisTitle?: string      // label on the Y axis
}

// Inherited from ChartBaseProps (canonical definition: Chart.Semantic.md §2.1):
// width?: string | number       default: '100%'
// height?: string | number      default: 300
// title?: string
// subtitle?: string
// legend?: boolean | ChartLegendProps
// tooltip?: boolean | ChartTooltipProps
// palette?: string[]
// transitions?: boolean
// className?: string
// style?: React.CSSProperties
// onSeriesClick?: (e: ChartSeriesClickEvent) => void
// onRender?: (e: ChartRenderEvent) => void
```

---

## 3. X-sort behavior

Each series' data is sorted by `x` ascending before mapping to ECharts `[x, y]` pairs. This ensures the connecting line does not cross itself for unsorted inputs. The sort is applied via `[...s.data].sort((a, b) => a.x - b.x)` — the original `data` array is not mutated.

---

## 4. Axis type

Both axes are numeric value axes (`type: 'value'`). This differs from `BarChart`/`LineChart` which use a category X axis. ECharts will auto-scale both axes to the data range.

---

## 5. Comparison with ScatterChart

| Feature | ScatterLineChart | ScatterChart |
|---|---|---|
| Series type | `'line'` (points connected) | `'scatter'` (isolated points) |
| X axis | numeric value | numeric value |
| `smooth` prop | yes | no |
| Point markers | ECharts default line markers | ECharts scatter symbols |

---

## 6. Composition notes

`ScatterLineChart` shares `ScatterDataItem` with `ScatterChart` (imported type). They are independent components — neither wraps the other.

---

## 7. Related components

- `ScatterChart` — isolated scatter points (no connecting lines)
- `LineChart` — category X axis; numeric Y axis; does not accept numeric X values

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SL1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
