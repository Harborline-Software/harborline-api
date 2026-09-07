# DrilldownChart — Semantic Contract

- **Component:** DrilldownChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DrilldownChart.Interaction.md) · [Accessibility](./DrilldownChart.Accessibility.md) · [Styling](./DrilldownChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/DrilldownChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Apache ECharts — dynamically imported via `echarts` npm package

---

## 1. Component purpose

**DrilldownChart** renders a hierarchical bar or line chart that allows users to click a category and navigate into a sub-level view of that category's data. Each click pushes a new level onto a navigation stack; a Back button allows navigating up one level at a time. The component is designed for hierarchical reporting scenarios (e.g., revenue by region → revenue by country → revenue by city).

---

## 2. Props

```typescript
interface DrilldownLevel {
  name: string                               // display name of the drilldown level
  data: ChartSeries[]                        // series data at this level
  categories?: Array<string | number>        // category labels at this level
}

interface DrilldownChartProps extends ChartBaseProps {
  initialSeries: ChartSeries[]               // top-level series data
  initialCategories: Array<string | number>  // top-level category labels
  drilldown: Record<string, DrilldownLevel>  // map from category name → drilldown level
  type?: 'column' | 'bar' | 'line'          // chart orientation/type; default: 'column'
  onDrilldown?: (category: string, level: number) => void  // fires after drilling into a category
  onDrillup?: (level: number) => void        // fires after navigating up one level
}

// Inherited from ChartBaseProps (canonical definition: Chart.Semantic.md §2.1):
// width?: string | number       default: '100%'
// height?: string | number      default: 300
// title?: string
// subtitle?: string
// legend?: boolean | ChartLegendProps
// tooltip?: boolean | ChartTooltipProps
// palette?: string[]
// transitions?: boolean
// className?: string
// style?: React.CSSProperties
// onSeriesClick?: (e: ChartSeriesClickEvent) => void
// onRender?: (e: ChartRenderEvent) => void
```

---

## 3. Navigation stack

The component maintains an internal `stack` array of `{ series, categories }` entries. At `stack.length === 0` the top-level `initialSeries` / `initialCategories` are shown. Each drilldown push appends the matching `DrilldownLevel`'s series and categories. Each drillup pops the last entry.

---

## 4. Type normalization

| `type` prop | Behavior |
|---|---|
| `'column'` (default) | Vertical bar chart — `xAxis` = category, `yAxis` = value |
| `'bar'` | Horizontal bar chart — `xAxis` = value, `yAxis` = category |
| `'line'` | Line chart — same axis layout as `'column'` |

Both `'column'` and `'bar'` map to ECharts `'bar'` series type; `'line'` maps to ECharts `'line'`.

---

## 5. Drilldown key matching

The `drilldown` record keys are matched against the `params.name` returned by ECharts on a click event. If no matching key exists for the clicked category, the click is a no-op (no navigation occurs).

---

## 6. Callbacks

- `onDrilldown(category, level)` — fired after the stack is pushed; `level` is the new stack depth (1-indexed).
- `onDrillup(level)` — fired during the stack pop; `level` is the resulting stack depth after the pop.

---

## 7. Composition notes

`DrilldownChart` uses `useChart` with the `instanceRef` to register and clean up the ECharts click handler on each re-render. The click handler is re-registered whenever `instanceRef`, `drilldown`, `stack.length`, or `onDrilldown` changes.

---

## 8. Related components

- `Chart` — generic chart without drilldown
- `BarChart`, `ColumnChart` — type-specific non-drilldown bar charts

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DC1 | Low | `legend?: boolean \| ChartLegendProps` union type is commented out — only `legend?: boolean` is active in M1; `ChartLegendProps` (position, formatter, itemStyle) reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
