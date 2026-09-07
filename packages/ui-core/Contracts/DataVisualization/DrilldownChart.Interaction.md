# DrilldownChart — Interaction Contract

- **Component:** DrilldownChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DrilldownChart.Semantic.md) · [Interaction](./DrilldownChart.Interaction.md) · [Accessibility](./DrilldownChart.Accessibility.md) · [Styling](./DrilldownChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/DrilldownChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Drilldown navigation (click a category)

Clicking a bar or line data point fires the ECharts `click` event handler. The handler looks up `drilldown[params.name]`. If a matching `DrilldownLevel` exists, its `data` and `categories` are pushed onto the internal `stack`. The ECharts option is recomputed with the new level's data and the chart re-renders. `onDrilldown(category, newLevel)` fires.

If no matching drilldown key exists for the clicked category, the click is silently ignored — no navigation, no error.

---

## 2. Back (drillup) navigation

A "← Back" button appears absolutely positioned at the top-left of the chart container whenever `stack.length > 0`. Clicking it pops the last stack entry and the chart re-renders with the previous level's data. `onDrillup(resultingLevel)` fires during the pop.

---

## 3. Hover tooltip

ECharts default tooltip on hover for bar and line types, showing category name and value. Behavior is identical to the base `Chart` tooltip for these types.

---

## 4. Legend

When `legend` is truthy, ECharts renders a legend. Clicking a legend item toggles the corresponding series visibility via standard ECharts behavior.

---

## 5. Click handler registration

The ECharts `click` handler is registered via `inst.on('click', handler)` inside a `useEffect`. The effect re-runs (and re-registers) when `instanceRef`, `drilldown`, `stack.length`, or `onDrilldown` changes. The previous handler is cleaned up via `inst.off('click', handler)` in the effect's cleanup function.

---

## 6. No zoom / pan

No zoom or pan is configured.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DRILL1 | Medium | No breadcrumb navigation — users cannot see their current drill path or jump directly to an ancestor level | Accepted-risk M1; add breadcrumb in M2 |
| G-DRILL2 | Medium | Drilldown is unlimited depth — there is no maximum level enforcement | Accepted-risk M1; callers ensure `drilldown` keys do not form cycles |
| G-DRILL3 | Low | `onDrillup` receives the level AFTER the pop (resulting depth), while `onDrilldown` receives the level AFTER the push — the semantics are asymmetric | Accepted-risk M1; document in API |
| G-DRILL4 | Low | No zoom/pan for large category sets at any level | Accepted-risk M1; caller supplies windowed slice |
