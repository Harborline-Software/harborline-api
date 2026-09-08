# DrilldownChart — Styling Contract

- **Component:** DrilldownChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DrilldownChart.Semantic.md) · [Interaction](./DrilldownChart.Interaction.md) · [Accessibility](./DrilldownChart.Accessibility.md) · [Styling](./DrilldownChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/DrilldownChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + consumer `className`. Inline `style` applies `{ width, height }` merged with the consumer `style` prop. Width defaults to `'100%'`; height defaults to `300` (px).

---

## 2. Chart inner container

A full-size inner `<div>` with `style={{ width: '100%', height: '100%' }}` holds the ECharts container. This fills the outer wrapper completely.

---

## 3. Back button

Absolutely positioned at `left-2 top-2`. Styles:

```
absolute left-2 top-2 rounded bg-white/80 px-2 py-1 text-xs shadow hover:bg-white
```

The button uses a fixed white/semi-transparent background regardless of theme. It is visible only when `stack.length > 0`.

---

## 4. Series colors

Series colors are set per-series via `s.color` → `itemStyle.color`. When `s.color` is absent, ECharts applies its default or `palette`-derived colors. Design-token palette variables are not directly referenced in the component.

---

## 5. Axes

For `type='column'` and `type='line'`: `xAxis` = category axis, `yAxis` = value axis.
For `type='bar'`: `xAxis` = value axis, `yAxis` = category axis.

Axis styling is delegated to ECharts defaults as configured by `buildBaseOption`.

---

## 6. Tooltip and legend

ECharts default tooltip and legend. Styling delegated to `buildBaseOption` and the ECharts theme.

---

## 7. Design tokens

The component directly references only Tailwind utility classes for the Back button (`bg-white/80`, `hover:bg-white`). All chart-internal tokens (`--chart-1` through `--chart-5`, `--border`, etc.) are delegated to the `buildBaseOption` / ECharts theme layer.
