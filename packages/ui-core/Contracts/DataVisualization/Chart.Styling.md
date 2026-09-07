# Chart — Styling Contract

- **Component:** Chart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chart.Semantic.md) · [Interaction](./Chart.Interaction.md) · [Accessibility](./Chart.Accessibility.md) · [Styling](./Chart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Chart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

The root `<div>` receives `className` (via `cn()`) and an inline `style` combining `{ width, height }` with the consumer-supplied `style` prop. No Tailwind utility classes are applied by default. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Series colors

Series colors are set per-series via `s.color` (passed as `itemStyle.color` to ECharts). When `s.color` is not set, ECharts applies its default color palette — or the `palette` prop if supplied to `buildBaseOption`. The design-token variables (`--chart-1` through `--chart-5`) are not directly referenced in the component source; their use depends on whether `buildBaseOption` injects them into the ECharts theme.

---

## 3. Area fill

When `type='area'`, each series receives `areaStyle: { opacity: 0.3 }`. Stroke color and area fill use the same series color.

---

## 4. Donut inner radius

When `type='donut'`, `radius: ['40%', '70%']` is applied. The inner 40% is hollow.

---

## 5. Axes

For Cartesian types, `xAxis` is a category axis and `yAxis` is a value axis. Axis styling is delegated to ECharts defaults as configured by `buildBaseOption`.

---

## 6. Tooltip

ECharts default tooltip; styled by the ECharts theme from `buildBaseOption`. No custom Tailwind tooltip overlay.

---

## 7. Legend

ECharts default legend when `legend` is truthy. Position and styling delegated to `buildBaseOption`.

---

## 8. Design tokens

The component does not directly reference Tailwind design tokens. Token consumption is deferred to the `buildBaseOption` / ECharts theme layer. Consumers who want design-token-aligned colors should pass a `palette` array derived from CSS variable resolution.
