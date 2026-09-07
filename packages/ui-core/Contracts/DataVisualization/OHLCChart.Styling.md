# OHLCChart — Styling Contract

- **Component:** OHLCChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OHLCChart.Semantic.md) · [Interaction](./OHLCChart.Interaction.md) · [Accessibility](./OHLCChart.Accessibility.md) · [Styling](./OHLCChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/OHLCChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Plain `<div>` receiving `className` (via `cn()`) and inline `style` combining `{ width, height }` with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Stick appearance

`barWidth: 1` on the ECharts candlestick series produces a 1-pixel-wide vertical stick. The thin stick approximates OHLC bar notation; ECharts renders open/close as the candlestick body clipped to 1 pixel width.

---

## 3. Rising / falling colors

Same as `CandlestickChart`:
- Rising: `risingColor` (default `#ec0000`) for fill and border
- Falling: `fallingColor` (default `#00da3c`) for fill and border

Direct hex values on ECharts `itemStyle`; not CSS variables.

---

## 4. Axes

X axis: category (dates). Y axis: value with `scale: true`. Axis styling delegated to ECharts defaults via `buildBaseOption`.

---

## 5. Tooltip and legend

ECharts defaults; styled by `buildBaseOption` ECharts theme.

---

## 6. Design tokens

No direct design-token references in the component source. Token consumption deferred to `buildBaseOption` / ECharts theme layer.
