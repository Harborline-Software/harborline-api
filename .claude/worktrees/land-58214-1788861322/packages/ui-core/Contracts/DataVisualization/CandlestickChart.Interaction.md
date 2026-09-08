# CandlestickChart — Interaction Contract

- **Component:** CandlestickChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CandlestickChart.Semantic.md) · [Interaction](./CandlestickChart.Interaction.md) · [Accessibility](./CandlestickChart.Accessibility.md) · [Styling](./CandlestickChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/CandlestickChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts provides a default crosshair tooltip on hover when `tooltip` is truthy (or when the `tooltip` prop is omitted and `buildBaseOption` supplies a default). The tooltip displays OHLC values for the hovered candle: date (from the X axis), open, close, low, high. Tooltip content is formatted by ECharts internally; no custom `format` function is wired.

---

## 2. Legend

The chart renders a single unnamed series. When `legend` is truthy, ECharts renders a legend entry for that series. Clicking the legend item toggles the candlestick series visibility — this is standard ECharts behavior, not custom logic.

---

## 3. Series click (`onSeriesClick`)

`CandlestickChart` inherits `onSeriesClick` from `ChartBaseProps` but does **not** wire a custom ECharts `click` event handler. The `onSeriesClick` callback is passed to `buildBaseOption` / `useChart` — whether it is wired depends on the `useChart` hook implementation. No explicit `inst.on('click', ...)` call appears in this component.

---

## 4. No drilldown

No drilldown navigation. Clicking a candle does not navigate to a sub-level.

---

## 5. No zoom / pan

No zoom or pan is configured in the ECharts option built by this component. Large datasets may need a windowed data slice supplied by the caller.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CAND1 | Medium | `showVolume` prop is declared but not implemented — calling code may expect a volume sub-chart | Accepted-risk M1; implement in a future wave |
| G-CAND2 | Low | Color defaults follow East Asian convention (red=rising, green=falling); no locale-aware default | Accepted-risk M1; callers override explicitly |
| G-CAND3 | Low | No zoom/pan — large OHLC datasets (>200 candles) are hard to navigate | Accepted-risk M1; caller supplies windowed slice |
