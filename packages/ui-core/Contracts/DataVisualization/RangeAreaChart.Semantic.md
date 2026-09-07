# RangeAreaChart — Semantic Contract

- **Component:** RangeAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./RangeAreaChart.Interaction.md) · [Accessibility](./RangeAreaChart.Accessibility.md) · [Styling](./RangeAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Range Area Chart baseline)
- **Catalog row:** #A16 RangeAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Component purpose

**RangeAreaChart** — renders a filled band between a `low` and `high` value per x-point. Used for confidence intervals, min/max temperature ranges, prediction envelopes.

---

## 2. Props (planned)

```typescript
interface RangeAreaDataPoint {
  x: string | number
  low: number
  high: number
}

interface RangeAreaSeries {
  data: RangeAreaDataPoint[]
  name?: string
  color?: string
  fillOpacity?: number    // default: 0.3
}

interface RangeAreaChartProps {
  series: RangeAreaSeries[]
  width?: number | string
  height?: number
  xAxisLabel?: string
  yAxisLabel?: string
  legend?: boolean
  tooltip?: boolean
  grid?: boolean
  className?: string
}
```

---

## 3. Band rendering

Each series renders as a filled area between `low` and `high` values. Upper boundary stroke + lower boundary stroke are rendered as separate lines framing the band.
