# BubbleChart — Semantic Contract

- **Component:** BubbleChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./BubbleChart.Interaction.md) · [Accessibility](./BubbleChart.Accessibility.md) · [Styling](./BubbleChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts ScatterChart with `size` encoding / Telerik Bubble Chart baseline)
- **Catalog row:** #A7 BubbleChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**BubbleChart** — a Cartesian scatter plot where each data point is rendered as a circle with a variable radius encoding a third numeric dimension (x, y, and size). Used for three-variable comparisons (e.g. revenue × margin × volume).

---

## 2. Props (planned)

```typescript
interface BubbleDataPoint {
  x: number
  y: number
  z: number           // controls bubble radius
  label?: string
  color?: string
  [key: string]: unknown
}

interface BubbleSeries {
  data: BubbleDataPoint[]
  name?: string
  color?: string
  zRange?: [number, number]   // min/max radius px; default: [10, 60]
}

interface BubbleChartProps {
  series: BubbleSeries[]
  width?: number | string
  height?: number            // default: 400
  xAxisLabel?: string
  yAxisLabel?: string
  xDomain?: [number | 'auto', number | 'auto']
  yDomain?: [number | 'auto', number | 'auto']
  legend?: boolean
  tooltip?: boolean
  grid?: boolean
  className?: string
}
```

---

## 3. Bubble sizing

`z` values are mapped to pixel radii within `zRange`. Mapping is linear by area (not radius), so perceptual size is proportional to the z value.

---

## 4. Labels

When `BubbleDataPoint.label` is set, a text label renders at the bubble center. Overlapping labels are not deconflicted in v1.

---

## 5. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
