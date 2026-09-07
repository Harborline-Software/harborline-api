# ScatterLineChart — Interaction Contract

- **Component:** ScatterLineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScatterLineChart.Semantic.md) · [Interaction](./ScatterLineChart.Interaction.md) · [Accessibility](./ScatterLineChart.Accessibility.md) · [Styling](./ScatterLineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ScatterLineChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts default tooltip on hover when `tooltip` is truthy. Shows the series name and `[x, y]` values at the hovered point. For value-axis charts, ECharts shows the exact numeric coordinates.

---

## 2. Legend

When `legend` is truthy, ECharts renders a legend with one entry per series. Clicking a legend item toggles that series' line visibility.

---

## 3. Series click

No custom ECharts click handler registered. `onSeriesClick` is inherited from `ChartBaseProps` and forwarded through `buildBaseOption` / `useChart`; whether it fires depends on `useChart` implementation.

---

## 4. No drilldown / no zoom

No drilldown or zoom behavior configured. `ScatterLineChart` is a read-only trend visualization.

---

## 5. Smooth toggle

`smooth` is a build-time option prop, not a runtime interactive toggle. Changing `smooth` causes a full re-render via `useMemo` dependency change; there is no animated transition between smooth and non-smooth states.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SL1 | Low | No zoom/pan — dense scatter-line datasets with many points may be hard to read | Accepted-risk M1; caller supplies windowed or sampled data |
| G-SL2 | Low | `smooth` is a static build option — no runtime toggle is exposed | Accepted-risk M1; callers control via prop |
