# RadarAreaChart — Semantic Contract

- **Component:** RadarAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./RadarAreaChart.Interaction.md) · [Accessibility](./RadarAreaChart.Accessibility.md) · [Styling](./RadarAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts RadarChart / Telerik Radar Chart baseline)
- **Catalog row:** #A13 RadarAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**RadarAreaChart** — renders one or more filled polygon overlays on a polar grid (spider chart). Each axis represents a data dimension; polygons show relative multi-dimensional performance. Used for skill matrices, attribute comparisons.

---

## 2. Props (planned)

```typescript
interface RadarSeries {
  dataKey: string
  name?: string
  color?: string
  fillOpacity?: number    // default: 0.3
}

interface RadarChartProps {
  data: Array<{ subject: string; [key: string]: number | string }>
  series: RadarSeries[]
  outerRadius?: number    // default: 80% of min(width,height)/2
  width?: number | string
  height?: number         // default: 300
  legend?: boolean
  tooltip?: boolean
  gridType?: 'polygon' | 'circle'   // default: 'polygon'
  className?: string
}
```

---

## 3. Data shape

Each `data` item represents one axis. The `subject` key is the axis label; each `series.dataKey` value is the magnitude on that axis.

---

## 4. Grid types

`gridType='polygon'`: concentric polygons for grid lines. `gridType='circle'`: concentric circles for grid lines.
