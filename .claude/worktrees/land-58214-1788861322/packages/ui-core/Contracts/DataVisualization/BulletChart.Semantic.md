# BulletChart — Semantic Contract

- **Component:** BulletChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./BulletChart.Interaction.md) · [Accessibility](./BulletChart.Accessibility.md) · [Styling](./BulletChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Bullet Chart / D3-bullet baseline)
- **Catalog row:** #A11 BulletChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Component purpose

**BulletChart** — a compact bar chart designed to replace gauges. Shows an actual value against a target marker and qualitative ranges (poor/satisfactory/good). Useful for KPI dashboards in small footprint.

---

## 2. Props (planned)

```typescript
interface BulletRange {
  from: number
  to: number
  label?: string       // e.g. 'Poor', 'Good'
  color?: string       // background color for this range band
}

interface BulletChartProps {
  value: number                 // the actual/current value bar
  target: number                // the comparative marker line
  ranges: BulletRange[]         // qualitative background bands (e.g. [{0,40,'Poor'},{40,70,'OK'},{70,100,'Good'}])
  min?: number                  // axis min; default: 0
  max?: number                  // axis max; default: max(ranges[last].to, value, target)
  title?: string
  subtitle?: string
  width?: number | string
  height?: number               // default: 40
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  tooltip?: boolean
  className?: string
}
```

---

## 3. Visual layers (bottom to top)

1. **Range bands**: colored background bands for qualitative context.
2. **Value bar**: the main performance bar (actual value).
3. **Target marker**: thin vertical line at the target value.

---

## 4. Multiple measures

Multiple BulletCharts can be stacked vertically in a dashboard — each is a standalone component.
