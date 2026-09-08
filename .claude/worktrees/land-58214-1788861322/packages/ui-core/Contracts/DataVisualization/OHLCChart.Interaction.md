# OHLCChart — Interaction Contract

- **Component:** OHLCChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OHLCChart.Semantic.md) · [Interaction](./OHLCChart.Interaction.md) · [Accessibility](./OHLCChart.Accessibility.md) · [Styling](./OHLCChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/OHLCChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts default tooltip on hover when `tooltip` is truthy. Shows date, open, close, low, high values for the hovered stick. Behavior is identical to `CandlestickChart`.

---

## 2. Legend

Single unnamed series; when `legend` is truthy, a legend entry is rendered. Clicking it toggles the series visibility.

---

## 3. Series click

`OHLCChart` does not register a custom ECharts click handler. `onSeriesClick` is inherited from `ChartBaseProps` and passed through `buildBaseOption` / `useChart`; whether it fires depends on `useChart` implementation.

---

## 4. No drilldown

No drilldown navigation.

---

## 5. No zoom / pan

No zoom or pan configured.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-OHLC1 | Medium | `showVolume` prop declared but not implemented | Accepted-risk M1; implement in a future wave |
| G-OHLC2 | Low | `barWidth: 1` achieves OHLC stick appearance via a candlestick series — ECharts does not have a native OHLC series type; the tick marks on open/close are absent | Accepted-risk M1; use a custom renderer or data-zoom series if authentic OHLC ticks are required |
| G-OHLC3 | Low | Color defaults follow East Asian convention; no locale-aware override | Accepted-risk M1; callers override explicitly |
| G-OHLC4 | Low | No zoom/pan for large datasets | Accepted-risk M1; caller supplies windowed slice |
