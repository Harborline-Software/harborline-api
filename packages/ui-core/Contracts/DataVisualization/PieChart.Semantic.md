# PieChart — Semantic Contract

- **Component:** PieChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./PieChart.Interaction.md) · [Accessibility](./PieChart.Accessibility.md) · [Styling](./PieChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart / Telerik Chart baseline)
- **Catalog row:** #A31 PieChart (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**PieChart** — renders a circular chart divided into wedge segments representing proportional values. Used for part-to-whole breakdowns (e.g. expense categories, market share). When `innerRadius > 0`, renders as a donut chart. See also: DonutChart (alias with preset `innerRadius`).

---

## 2. Props (planned)

```typescript
interface PieSegment {
  name: string
  value: number
  color?: string
}

interface PieChartProps {
  data: PieSegment[]
  width?: number | string    // default: '100%'
  height?: number            // default: 300
  innerRadius?: number       // donut hole radius (0 = full pie); default: 0
  outerRadius?: number       // slice radius; default: auto (80% of min(w,h)/2)
  startAngle?: number        // first slice start angle; default: 90 (top)
  paddingAngle?: number      // gap between slices in degrees; default: 0
  label?: boolean | ((entry: PieSegment) => string)  // default: false
  legend?: boolean           // default: true
  tooltip?: boolean          // default: true
  centerLabel?: React.ReactNode  // content in donut center hole (only when innerRadius > 0)
  className?: string
}
```

---

## 3. Segment coloring

Segments are colored by `PieSegment.color` or the chart palette `hsl(var(--chart-N))` in sequence.

---

## 4. Labels

When `label=true`: each segment renders a percentage or value label outside the slice. When `label` is a function: return value is rendered as the label string.

---

## 5. Center label (donut)

When `innerRadius > 0` and `centerLabel` is set, the node renders centered in the donut hole — typically used for a total value or summary stat.
