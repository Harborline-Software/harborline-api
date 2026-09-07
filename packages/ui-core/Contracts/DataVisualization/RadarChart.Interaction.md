# RadarChart — Interaction Contract

- **Component:** RadarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadarChart.Semantic.md) · [Interaction](./RadarChart.Interaction.md) · [Accessibility](./RadarChart.Accessibility.md) · [Styling](./RadarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/RadarChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts default tooltip on hover when `tooltip` is truthy. Shows the series name and the values for each indicator at the hovered point. Tooltip follows the cursor.

---

## 2. Legend

When `legend` is truthy, ECharts renders a legend with one entry per series. Clicking a legend item toggles that series' polygon visibility.

---

## 3. Series click

No custom ECharts click handler is registered. `onSeriesClick` is inherited from `ChartBaseProps` and forwarded through `buildBaseOption` / `useChart`; whether it fires depends on `useChart` implementation. There is no per-series or per-axis click callback.

---

## 4. No drilldown / no zoom

No drilldown or zoom behavior. `RadarChart` is a read-only overlay visualization.

---

## 5. `useMemo` dependency

The option is re-computed only when `rest` (the spread of non-layout props) changes. Layout props (`width`, `height`, `className`, `style`) are excluded from the memo dependency. This means indicator or series data changes will trigger a new ECharts option update.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RADAR1 | Low | No per-indicator click or highlight — users cannot isolate a single axis | Accepted-risk M1; ECharts radar does not natively support per-axis click |
| G-RADAR2 | Low | `max` omitted from any indicator causes ECharts auto-scaling — different datasets may produce inconsistent axis ranges | Accepted-risk M1; callers supply explicit `max` when consistent scales matter |
