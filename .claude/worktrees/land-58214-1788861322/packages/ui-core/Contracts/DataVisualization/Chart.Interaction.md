# Chart — Interaction Contract

- **Component:** Chart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chart.Semantic.md) · [Interaction](./Chart.Interaction.md) · [Accessibility](./Chart.Accessibility.md) · [Styling](./Chart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Chart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts provides a default tooltip on hover when `tooltip` is truthy (or not overridden). For Cartesian types the tooltip shows the category and all series values at the hovered position. For non-Cartesian types the tooltip shows the segment name and value.

---

## 2. Legend interaction

When `legend` is truthy, ECharts renders a legend. Clicking a legend item toggles the corresponding series visibility. This is standard ECharts behavior applied to all chart types.

---

## 3. Series click (`onSeriesClick`)

`Chart` inherits `onSeriesClick` from `ChartBaseProps` and passes it through `buildBaseOption` / `useChart`. Whether the click callback fires depends on the `useChart` hook implementation. No additional per-type click logic is registered in the `Chart` component source.

---

## 4. Stacked series

When `stacked=true`, all Cartesian series share `stack: 'total'`. The tooltip reflects stacked values. No interactive stack-toggle control is provided.

---

## 5. No drilldown

`Chart` does not support drilldown navigation. Use `DrilldownChart` when hierarchical click-through is needed.

---

## 6. No zoom / pan

No zoom or pan is configured. Large datasets should be windowed by the caller.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CHART1 | Medium | `type='bubble'`, `type='heatmap'`, `type='radar'`, `type='sankey'` are listed in `ChartSeriesType` but the option builder only handles Cartesian + pie/donut/funnel paths — non-handled types fall through to an unmapped ECharts series type | Accepted-risk M1; use dedicated specialized components for these types |
| G-CHART2 | Low | `type='ohlc'` maps to ECharts `candlestick` but `ChartSeries[]` carries no OHLC structure — actual OHLC data cannot be passed through `Chart` | Accepted-risk M1; use `OHLCChart` for OHLC data |
| G-CHART3 | Low | No zoom/pan for large Cartesian datasets | Accepted-risk M1; caller supplies windowed slice |
