# RadarChart — Semantic Contract

- **Component:** RadarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./RadarChart.Interaction.md) · [Accessibility](./RadarChart.Accessibility.md) · [Styling](./RadarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/RadarChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**RadarChart** renders a spider/radar chart that compares multiple series across a fixed set of named axes (indicators). Each series is plotted as a polygon connecting its values on each axis. It is used for comparing entities across multiple dimensions simultaneously — for example, comparing products across price, quality, availability, and support dimensions, or evaluating employee performance profiles.

---

## 2. Props

```typescript
interface RadarIndicator {
  name: string    // axis label
  max?: number    // maximum value for this axis; controls the axis scale
}

interface RadarSeries {
  name: string          // series name (shown in legend and tooltip)
  data: number[]        // values in the same order as `indicators`
  color?: string        // per-series color override
}

interface RadarChartProps extends ChartBaseProps {
  indicators: RadarIndicator[]    // defines the axes (spokes) of the radar
  series: RadarSeries[]           // one or more series to overlay on the radar
  shape?: 'polygon' | 'circle'    // grid shape; default: 'polygon'
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

## 3. Indicator axes

Each `RadarIndicator` defines one spoke of the radar. The `name` is displayed as the axis label. The `max` value sets the outer bound of that axis — when omitted, ECharts auto-scales based on the maximum value across all series for that axis. Values in `RadarSeries.data` must be in the same positional order as `indicators`.

---

## 4. Grid shape

`shape: 'polygon'` (default) draws a polygonal grid. `shape: 'circle'` draws concentric circles. The shape affects only the grid lines, not the series polygons.

---

## 5. Multi-series overlay

Multiple `RadarSeries` entries are overlaid on the same radar grid. Each series renders as a filled polygon with a border line. Per-series color is applied to both `itemStyle.color` and `lineStyle.color`.

---

## 6. Composition notes

The option is built via the `buildRadarOption` helper function (private to the module) which calls `buildBaseOption` and appends the `radar` and `series` ECharts config. The component does not expose `buildRadarOption` publicly.

---

## 7. Related components

- `RadarAreaChart` — pre-existing contract; area-filled radar variant
- `RadarColumnChart` — pre-existing contract; column-grid radar variant
- `Chart` with `type='radar'` — passes data through `ChartSeries[]` but the option builder does not include a radar-specific axis config, so it may not render correctly

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RC1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
