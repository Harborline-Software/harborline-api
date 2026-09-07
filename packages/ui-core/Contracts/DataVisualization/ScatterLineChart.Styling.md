# ScatterLineChart — Styling Contract

- **Component:** ScatterLineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScatterLineChart.Semantic.md) · [Interaction](./ScatterLineChart.Interaction.md) · [Accessibility](./ScatterLineChart.Accessibility.md) · [Styling](./ScatterLineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ScatterLineChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Plain `<div>` receiving `className` (via `cn()`) and inline `style` combining `{ width, height }` with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Series rendering

Each series renders as an ECharts `line` type. Points are connected in X-ascending order. `smooth: true` applies Catmull-Rom interpolation to the connecting line; `smooth: false` (default) draws straight segments.

---

## 3. Series colors

Per-series color via `s.color` → `itemStyle.color`. When absent, ECharts default or `palette`-derived colors apply. Design-token variables not referenced directly.

---

## 4. Axes

Both axes are numeric value axes. `xAxisTitle` and `yAxisTitle` are set as the ECharts axis `name` property and rendered as axis labels by ECharts. Axis styling delegated to ECharts defaults via `buildBaseOption`.

---

## 5. Tooltip and legend

ECharts defaults; styled by `buildBaseOption` ECharts theme.

---

## 6. Design tokens

No direct design-token references in the component source. Token consumption deferred to `buildBaseOption` / ECharts theme layer.
