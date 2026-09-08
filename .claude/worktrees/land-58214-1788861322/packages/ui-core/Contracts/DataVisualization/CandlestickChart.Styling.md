# CandlestickChart — Styling Contract

- **Component:** CandlestickChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CandlestickChart.Semantic.md) · [Interaction](./CandlestickChart.Interaction.md) · [Accessibility](./CandlestickChart.Accessibility.md) · [Styling](./CandlestickChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/CandlestickChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

The root `<div>` receives `className` (via `cn()`) and an inline `style` combining `{ width, height }` with the consumer-supplied `style` prop. No Tailwind utility classes are applied by default. Width defaults to `'100%'`; height defaults to `300` (px as a numeric inline style).

---

## 2. Candle colors

Rising candles (close ≥ open): fill and border use `risingColor` (default `#ec0000`).
Falling candles (close < open): fill and border use `fallingColor` (default `#00da3c`).

These are direct hex values passed to ECharts `itemStyle.color` / `itemStyle.borderColor`; they are not CSS variables. The design-token chart palette (`--chart-1` through `--chart-5`) is not used for candle coloring.

---

## 3. Palette

The `palette` prop (inherited from `ChartBaseProps`) is passed to `buildBaseOption` and applied as the ECharts global color palette. It does not affect candle body colors (which are always `risingColor` / `fallingColor`), but may affect tooltip UI elements if overridden by `buildBaseOption`.

---

## 4. Axes

X axis: category axis rendering dates as strings. Y axis: value axis with `scale: true` (no forced zero baseline). Axis styling is inherited from ECharts defaults as configured by `buildBaseOption`.

---

## 5. Tooltip

ECharts default tooltip; styled by the ECharts theme applied via `buildBaseOption`. No custom Tailwind tooltip overlay is used.

---

## 6. Legend

ECharts default legend when `legend` is truthy; positioned and styled by `buildBaseOption`.

---

## 7. Design tokens

The chart does not directly reference Tailwind design tokens (`--chart-*`, `--border`, `--muted-foreground`, etc.) in the component source. Token consumption is deferred to the `buildBaseOption` / ECharts theme layer.
