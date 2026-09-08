# PyramidChart — Styling Contract

- **Component:** PyramidChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PyramidChart.Semantic.md) · [Interaction](./PyramidChart.Interaction.md) · [Accessibility](./PyramidChart.Accessibility.md) · [Styling](./PyramidChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/PyramidChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Plain `<div>` receiving `className` (via `cn()`) and inline `style` combining `{ width, height }` with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Segment shape

ECharts `funnel` series with `sort: 'ascending'` — smallest segment at top, largest at bottom. Produces a triangular pyramid shape (widest tier is at the base).

---

## 3. Segment colors

Per-segment color via `d.color` → `itemStyle.color`. When absent, ECharts default or `palette`-derived colors apply. Design-token variables not referenced directly.

---

## 4. No custom gap or orientation

`gap`, `orient`, and `direction` are not exposed. ECharts defaults for funnel segment spacing apply.

---

## 5. Tooltip and legend

ECharts defaults; styled by `buildBaseOption` ECharts theme.

---

## 6. Design tokens

No direct design-token references in the component source. Token consumption deferred to `buildBaseOption` / ECharts theme layer.
