# PolarChart — Semantic Contract

- **Component:** PolarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./PolarChart.Interaction.md) · [Accessibility](./PolarChart.Accessibility.md) · [Styling](./PolarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A22 PolarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; ECharts polar/Recharts RadialBar baseline)

---

## 1. Component purpose

**PolarChart** — a circular chart that plots data along radial axes. Supports two subtypes: `rose` (Nightingale / wind-rose, where bar length encodes value) and `radialBar` (stacked radial bars emanating from center). Distinct from RadarChart (which uses multi-axis polygon overlay) and DonutChart (which uses pie sectors).

---

## 2. Props

```typescript
interface PolarSeries {
  dataKey: string
  name?: string
  color?: string
  stackId?: string
}

interface PolarChartProps {
  data: ChartDataPoint[]
  series: PolarSeries[]
  type?: 'rose' | 'radialBar'
  angleDataKey?: string
  innerRadius?: number | string
  outerRadius?: number | string
  startAngle?: number
  endAngle?: number
  legend?: boolean
  tooltip?: boolean
  width?: number | string
  height?: number
  className?: string
}
```

`ChartDataPoint` is `{ [key: string]: string | number }` — same as AreaChart/BarChart family.

---

## 3. Type distinctions

- `rose` — equal-angle sectors, bar length (radius) encodes value. Suitable for cyclical data (months, compass directions).
- `radialBar` — bars start at `innerRadius` and extend outward. Length encodes value as percentage of full radius. Suitable for part-to-whole comparisons.

---

## 4. Angle behavior

`startAngle` defaults to 90 (top of circle). `endAngle` defaults to -270 (full circle, clockwise). Partial polar sweeps supported by setting a narrower range.

---

## 5. Relationship to other charts

Use RadarAreaChart/RadarColumnChart for multi-axis performance comparisons. Use PolarChart for single-axis circular frequency or radial-bar comparisons.
