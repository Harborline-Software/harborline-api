# StockChart — Semantic Contract

- **Component:** StockChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./StockChart.Interaction.md) · [Accessibility](./StockChart.Accessibility.md) · [Styling](./StockChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik StockChart / KendoReact StockChart baseline)
- **Catalog row:** #A15 StockChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik StockChart baseline)

---

## 1. Component purpose

**StockChart** — a specialized financial time-series chart combining candlestick or OHLC bars with optional overlay indicators (moving averages, Bollinger bands) and a navigator sub-chart for zooming into a time range.

---

## 2. Props (planned)

```typescript
interface OHLCDataPoint {
  date: Date | string
  open: number
  high: number
  low: number
  close: number
  volume?: number
}

interface StockChartProps {
  data: OHLCDataPoint[]
  type?: 'candlestick' | 'ohlc' | 'line'   // default: 'candlestick'
  width?: number | string
  height?: number                            // default: 400
  navigator?: boolean                        // show range-selector sub-chart; default: true
  navigatorHeight?: number                   // default: 60
  volume?: boolean                           // show volume bars below; default: false
  indicators?: StockIndicator[]             // MA, EMA, Bollinger, RSI, etc.
  tooltip?: boolean
  legend?: boolean
  className?: string
}
```

---

## 3. Candlestick vs OHLC

`type='candlestick'`: colored filled rectangles (body) + wicks. `type='ohlc'`: open/close tick lines + high/low vertical bar. `type='line'`: close-price line only.

---

## 4. Navigator

Sub-chart at bottom provides a mini-view of the full data range. Dragging the selection window changes the visible x-axis range on the main chart.

---

## 5. Indicators

Overlay line series: MA (simple moving average), EMA (exponential), Bollinger bands (upper/lower bands). Each indicator takes a `period` parameter.
