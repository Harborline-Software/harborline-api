# ChartWizard — Semantic Contract

- **Component:** ChartWizard
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ChartWizard.Interaction.md) · [Accessibility](./ChartWizard.Accessibility.md) · [Styling](./ChartWizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartWizard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**ChartWizard** is a self-contained chart-type explorer that renders the same dataset in multiple chart types through a type-selector toolbar. The user clicks a button to switch between chart types; the selected type is applied to a `Chart` component rendered below the toolbar. It is intended for dashboards and exploratory data views where end-users benefit from switching between line, bar, area, scatter, pie, donut, and funnel representations of the same data without writing code.

---

## 2. Props

```typescript
interface ChartWizardProps {
  series: ChartSeries[]                          // data series passed to Chart
  categories?: Array<string | number | Date>     // X-axis categories passed to Chart; default: []
  defaultType?: ChartSeriesType                  // active chart type on mount; default: 'line'
  allowedTypes?: ChartSeriesType[]               // subset of types to show in toolbar; default: all 7
  width?: string | number                        // passed to Chart; default: '100%'
  height?: string | number                       // passed to Chart; default: 300
  className?: string                             // applied to outer wrapper div
}
```

---

## 3. Toolbar types

The full default type set is: `line`, `bar`, `area`, `scatter`, `pie`, `donut`, `funnel` (7 types). This is a curated subset of `ChartSeriesType` — the more exotic types (`bubble`, `heatmap`, `candlestick`, `ohlc`, `radar`, `sankey`) are intentionally excluded from the wizard toolbar.

When `allowedTypes` is provided, the toolbar is filtered to only the listed types (preserving the display order from the default list). Types in `allowedTypes` that are not in the default 7 are silently ignored (the filter operates on the `CHART_TYPES` constant, not the full `ChartSeriesType` union).

---

## 4. Active type state

`activeType` is local React state initialized from `defaultType`. It is not controlled — the parent cannot update the active type after mount via props. There is no `onTypeChange` callback.

---

## 5. Composition notes

`ChartWizard` composes `Chart` as its rendering engine. All chart-rendering behavior (ECharts rendering, tooltip, legend, series click) is delegated to `Chart`. `ChartWizard` does not accept `ChartBaseProps` directly — it has its own narrower prop set (`series`, `categories`, `defaultType`, `allowedTypes`, `width`, `height`, `className`). Title, subtitle, legend, tooltip, palette, and transitions props are not forwarded to `Chart`.

---

## 6. Related components

- `Chart` — the underlying rendering engine; accepts the full `ChartBaseProps` including title/legend/tooltip/palette
- `BarChart`, `LineChart`, `PieChart`, etc. — type-specific components for use when the type is fixed
