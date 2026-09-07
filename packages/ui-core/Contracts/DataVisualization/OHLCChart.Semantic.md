# OHLCChart — Semantic Contract

- **Component:** OHLCChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./OHLCChart.Interaction.md) · [Accessibility](./OHLCChart.Accessibility.md) · [Styling](./OHLCChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/OHLCChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**OHLCChart** renders a financial Open-High-Low-Close chart as thin vertical sticks (OHLC bars) on a Cartesian grid. It shares the same props type and data shape as `CandlestickChart` but renders each period as a narrow stick with horizontal tick marks rather than a filled candle body. The OHLC stick representation is preferred in contexts where space is limited or where traders expect traditional bar-chart notation rather than Japanese candlestick notation.

---

## 2. Props

```typescript
// OHLCChartProps is a type alias for CandlestickChartProps.
// OHLCDataPoint — canonical definition: StockChart.Semantic.md §2. Shared with
// CandlestickChart. OHLCChart does not consume volume? — that field is ignored.

type OHLCChartProps = CandlestickChartProps

// Which expands to:
interface OHLCChartProps extends ChartBaseProps {
  data: OHLCDataPoint[]
  risingColor?: string      // default: '#ec0000'
  fallingColor?: string     // default: '#00da3c'
  showVolume?: boolean      // declared; not implemented
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

## 3. Rendering difference from CandlestickChart

`OHLCChart` uses the same ECharts `candlestick` series type as `CandlestickChart` but sets `barWidth: 1` to achieve a thin-stick OHLC appearance. The data encoding is identical: `[open, close, low, high]`.

---

## 4. Color convention

Same as `CandlestickChart`: rising (`close >= open`) uses `risingColor` (default `#ec0000`); falling (`close < open`) uses `fallingColor` (default `#00da3c`). Same international vs. Western color convention note applies.

---

## 5. `showVolume` prop

Declared on the interface (via `CandlestickChartProps`) but not consumed. No-op.

---

## 6. Y-axis scaling

`scale: true` — same as `CandlestickChart`; axis does not force a zero baseline.

---

## 7. Related components

- `CandlestickChart` — same props, renders as filled candle bodies
- `Chart` with `type='ohlc'` — maps to ECharts candlestick but only accepts `ChartSeries[]` data shape
- `StockChart` — full stock chart with navigator

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-OH1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
