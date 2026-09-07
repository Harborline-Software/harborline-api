# PyramidChart — Semantic Contract

- **Component:** PyramidChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PyramidChart.Interaction.md) · [Accessibility](./PyramidChart.Accessibility.md) · [Styling](./PyramidChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/PyramidChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**PyramidChart** renders a pyramid-shaped funnel visualization where the smallest segment appears at the top and the largest at the bottom — the inverse of a standard funnel. It is used to visualize hierarchical or proportional data where the base represents the largest group (e.g., population pyramids, organizational hierarchies, sales qualification from opportunity to close when viewed bottom-up).

---

## 2. Props

```typescript
// PyramidChart reuses FunnelDataItem from FunnelChart:
interface FunnelDataItem {
  name: string
  value: number
  color?: string    // per-segment color override
}

interface PyramidChartProps extends ChartBaseProps {
  data: FunnelDataItem[]
  onItemClick?: (item: FunnelDataItem) => void
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

## 3. Rendering

`PyramidChart` uses ECharts `funnel` series type with `sort: 'ascending'`. Ascending sort places the smallest value at the top, producing the pyramid shape. This is the only difference from `FunnelChart`, which defaults to `sort: 'descending'`.

---

## 4. Segment colors

Each segment's color is controlled by `d.color` → `itemStyle.color`. When `color` is absent on a data item, ECharts applies the default or `palette`-derived color.

---

## 5. No `direction` / `orient` / `gap` props

Unlike `FunnelChart`, `PyramidChart` does not expose `direction`, `orient`, or `gap` props. The pyramid is always vertical, ascending, with default ECharts gap.

---

## 6. Composition notes

`PyramidChart` is a specialized wrapper over the ECharts `funnel` series. It shares `FunnelDataItem` with `FunnelChart` — the two components use the same data shape with different sort orders.

---

## 7. Related components

- `FunnelChart` — descending funnel (widest at top)
- `Chart` with `type='funnel'` — generic funnel via `ChartSeries[]`

---

## 8. PL3-18 disposition — data shape vs PL3-8 categorical family

See [FunnelChart.Semantic.md §5](./FunnelChart.Semantic.md) for the authoritative PL3-18 disposition. Summary: PyramidChart uses per-item `{ name: string; value: number }` objects — not the `categoryKey + ChartDataPoint[]` shape — because funnel/pyramid segments have no shared linear categorical axis. The `name` field is the segment label; `value` is always numeric. No prop rename needed.

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PY1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
