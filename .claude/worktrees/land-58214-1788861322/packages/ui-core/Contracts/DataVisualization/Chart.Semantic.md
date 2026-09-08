# Chart — Semantic Contract

- **Component:** Chart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Chart.Interaction.md) · [Accessibility](./Chart.Accessibility.md) · [Styling](./Chart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Chart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**Chart** is a single-component multi-type adapter that renders any of thirteen ECharts series types through a unified `type` prop and a `ChartSeries[]` data contract. It normalizes the API surface across Cartesian types (line, bar, area, scatter) and non-Cartesian types (pie, donut, bubble, heatmap, candlestick, ohlc, funnel, radar, sankey), delegating all rendering to Apache ECharts via the `useChart` hook. `Chart` is intended as a convenience wrapper; specialized chart components (e.g., `CandlestickChart`, `RadarChart`) accept richer type-specific props and should be preferred when per-type configuration is needed.

---

## 2. Props

```typescript
type ChartSeriesType =
  | 'line' | 'bar' | 'area' | 'scatter' | 'pie' | 'donut'
  | 'bubble' | 'heatmap' | 'candlestick' | 'ohlc' | 'funnel'
  | 'radar' | 'sankey'

interface ChartProps extends ChartBaseProps {
  type: ChartSeriesType         // required — selects the ECharts series type
  series: ChartSeries[]         // data series; each: { name, data: ChartDataItem[], color? }
  categories?: Array<string | number | Date>  // X-axis labels for Cartesian types; default: []
  stacked?: boolean             // stack Cartesian series on a shared 'total' stack; default: false
}

// Inherited from ChartBaseProps (defined below, §2.1):
// width, height, title, subtitle, legend, tooltip, palette, transitions,
// className, style, onSeriesClick, onRender
```

### 2.1 ChartBaseProps — canonical definition

All chart components inherit from `ChartBaseProps`. This is the **authoritative definition** — all chart contracts that reference `ChartBaseProps` refer to this shape:

```typescript
interface ChartBaseProps {
  width?: string | number           // default: '100%'
  height?: string | number          // default: 300
  title?: string
  subtitle?: string
  legend?: boolean | ChartLegendProps
  tooltip?: boolean | ChartTooltipProps
  palette?: string[]
  transitions?: boolean             // default: true; respects prefers-reduced-motion
  className?: string
  style?: React.CSSProperties
  onSeriesClick?: (e: ChartSeriesClickEvent) => void
  onRender?: (e: ChartRenderEvent) => void
}
```

---

## 3. Type normalization

The `type` prop is mapped to an ECharts series type before the option is built:

| `type` prop | ECharts series type | Notes |
|---|---|---|
| `'area'` | `'line'` | `areaStyle: { opacity: 0.3 }` added to each series |
| `'donut'` | `'pie'` | `radius: ['40%', '70%']` added to each series |
| `'ohlc'` | `'candlestick'` | Same ECharts type as candlestick |
| all others | same value | No normalization |

---

## 4. Cartesian vs. non-Cartesian branching

Cartesian types (`line`, `bar`, `area`, `scatter`) render with explicit `xAxis` (category, using `categories`) and `yAxis` (value) definitions. Non-Cartesian types omit axis definitions; each series data item is mapped to `{ name: String(d.category), value: d.value }`.

The `stacked` prop is only meaningful for Cartesian types; non-Cartesian types ignore it.

---

## 5. Variants and states

- **Multi-series:** multiple `series` entries rendered simultaneously (overlapping for area/line, grouped for bar, etc.)
- **Stacked:** when `stacked=true`, all Cartesian series share `stack: 'total'`
- **Single-series non-Cartesian:** pie, donut, funnel, radar, sankey — typically one series with named data items

---

## 6. Composition notes

`Chart` composes `buildBaseOption` and `useChart` internally. `ChartWizard` wraps `Chart` and adds a type-selector toolbar. Specialized chart components (`CandlestickChart`, `OHLCChart`, `RadarChart`, `Sankey`, `PyramidChart`, etc.) each accept type-specific props not available through `Chart`.

---

## 7. Related components

- `ChartWizard` — wraps `Chart` with an interactive type-selector toolbar
- `CandlestickChart` — dedicated OHLC candlestick with `CandlestickDataItem[]` props
- `OHLCChart` — dedicated OHLC stick chart
- `RadarChart` — dedicated radar with `RadarIndicator[]` + `RadarSeries[]`
- `Sankey` — dedicated Sankey flow chart with explicit node/link types
- `PyramidChart` — dedicated pyramid (ascending funnel)
