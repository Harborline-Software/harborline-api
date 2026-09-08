# RadarChart — Styling Contract

- **Component:** RadarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadarChart.Semantic.md) · [Interaction](./RadarChart.Interaction.md) · [Accessibility](./RadarChart.Accessibility.md) · [Styling](./RadarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/RadarChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Plain `<div>` receiving `className` (via `cn()`) and inline `style` combining `{ width, height }` with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Grid shape

`shape: 'polygon'` (default) — polygonal grid lines connecting each indicator axis. `shape: 'circle'` renders concentric circular grid lines. No visual difference in series polygon rendering.

---

## 3. Series colors

Per-series color via `s.color` → `itemStyle.color` + `lineStyle.color`. Both the polygon fill and the outline border use the same color value. When `s.color` is absent, ECharts default or `palette`-derived colors apply.

---

## 4. Tooltip and legend

ECharts defaults; styled by `buildBaseOption` ECharts theme.

---

## 5. Indicator axis labels

Axis labels (indicator names) are rendered by ECharts at each spoke tip. Styling is controlled by the ECharts theme from `buildBaseOption`.

---

## 6. Design tokens

No direct design-token references in the component source. Token consumption deferred to `buildBaseOption` / ECharts theme layer.
