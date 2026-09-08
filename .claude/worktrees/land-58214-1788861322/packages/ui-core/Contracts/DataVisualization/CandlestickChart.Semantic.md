# CandlestickChart — Semantic Contract

- **Component:** CandlestickChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CandlestickChart.Interaction.md) · [Accessibility](./CandlestickChart.Accessibility.md) · [Styling](./CandlestickChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/CandlestickChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**CandlestickChart** renders a financial candlestick series on a Cartesian grid with a category X axis (dates) and a scaled numeric Y axis. Each data point encodes four OHLC values (open, high, low, close) as a traditional candle body with wicks. Rising candles (close ≥ open) and falling candles (close < open) are distinguished by color. The component delegates all rendering to Apache ECharts via the `useChart` hook and exposes no direct chart-library API surface.

---

## 2. Props

```typescript
// OHLCDataPoint — canonical definition shared with StockChart and OHLCChart.
// See [StockChart.Semantic.md](./StockChart.Semantic.md) §2 for the authoritative type definition.
// CandlestickChart does not consume volume? — that field is ignored.
type CandlestickDataItem = OHLCDataPoint  // type alias; use OHLCDataPoint in new code

interface CandlestickChartProps extends ChartBaseProps {
  data: OHLCDataPoint[]
  risingColor?: string      // default: '#ec0000' (red)  — candle where close >= open
  fallingColor?: string     // default: '#00da3c' (green) — candle where close < open
  // showVolume?: boolean   // declared in props interface but not yet wired in option build
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

## 3. Data encoding

ECharts candlestick encoding order is `[open, close, low, high]`. The implementation maps each `OHLCDataPoint` as `[d.open, d.close, d.low, d.high]` — the high/low swap from the raw prop order is intentional and matches the ECharts `candlestick` series convention.

---

## 4. Color convention

The `risingColor` / `fallingColor` defaults follow international financial chart convention (red = rising, green = falling) rather than Western convention. Consumers in Western-market contexts should override to `risingColor="#22c55e"` (green) and `fallingColor="#ef4444"` (red).

---

## 5. `showVolume` prop

`showVolume` is declared on the `CandlestickChartProps` interface but is not consumed in the `option` build. It is a no-op in the current implementation. Volume sub-chart rendering is deferred.

---

## 6. Y-axis scaling

The Y axis uses `scale: true`, which clips the visible range to the actual data range rather than forcing the axis to start at zero. This is the correct default for price charts.

---

## 7. Composition notes

`CandlestickChart` is a leaf component; it does not compose other chart components. The `OHLCChart` sibling shares the same props type (`CandlestickChartProps`) and data shape but renders as thin sticks (OHLC bars) by setting `barWidth: 1`.

---

## 8. Related components

- `OHLCChart` — same data shape, renders as OHLC sticks instead of candle bodies
- `Chart` — generic adapter; maps `type='candlestick'` to the same ECharts candlestick series (but only accepts `ChartSeries[]` not `OHLCDataPoint[]`)
- `StockChart` — full stock chart with navigator and multiple indicator series

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CC1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
