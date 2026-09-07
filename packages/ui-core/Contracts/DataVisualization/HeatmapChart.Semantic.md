# HeatmapChart — Semantic Contract

- **Component:** HeatmapChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./HeatmapChart.Interaction.md) · [Accessibility](./HeatmapChart.Accessibility.md) · [Styling](./HeatmapChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik HeatMap / D3 heatmap baseline)
- **Catalog row:** #A10 HeatmapChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Component purpose

**HeatmapChart** — a grid of colored cells where each cell's color intensity represents a numeric value. Used for activity calendars, correlation matrices, and time-of-day/day-of-week density views.

---

## 2. Props (planned)

```typescript
interface HeatmapCell {
  x: string | number      // column key
  y: string | number      // row key
  value: number
}

interface HeatmapChartProps {
  data: HeatmapCell[]
  xLabels: (string | number)[]   // ordered column labels
  yLabels: (string | number)[]   // ordered row labels
  width?: number | string
  height?: number
  colorScale?: [string, string]  // [low-color, high-color]; default: theme low→high
  valueRange?: [number, number]  // domain for color mapping; default: data min/max
  cellSize?: number              // px per cell; default: auto-fit
  tooltip?: boolean
  legend?: boolean               // color-scale legend strip; default: true
  className?: string
}
```

---

## 3. Color scale

Value → color mapping uses a linear interpolation between `colorScale[0]` (minimum) and `colorScale[1]` (maximum). Null/missing cells render as the background color.

---

## 4. Legend

A horizontal color gradient strip with min/max labels renders below the chart.
