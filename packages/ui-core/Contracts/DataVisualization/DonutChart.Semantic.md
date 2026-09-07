# DonutChart — Semantic Contract

- **Component:** DonutChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./DonutChart.Interaction.md) · [Accessibility](./DonutChart.Accessibility.md) · [Styling](./DonutChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart with innerRadius / Telerik Donut Chart baseline)
- **Catalog row:** #A5 DonutChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**DonutChart** — a PieChart variant with a configurable inner radius creating a "donut hole." Commonly used to display a summary value in the center (total, percentage, KPI). Thin wrapper around PieChart with `innerRadius` preset.

---

## 2. Props (planned)

```typescript
interface DonutChartProps extends Omit<PieChartProps, 'innerRadius'> {
  innerRadius?: number       // default: 60% of outerRadius
  centerLabel?: React.ReactNode  // content in donut center (inherited from PieChart)
}
```

Inherits all props from `PieChartProps`. See `PieChart.Semantic.md` for the full interface.

---

## 3. Relationship to PieChart

DonutChart is PieChart with `innerRadius` defaulting to 60% of `outerRadius`. No additional behavior. Exists as a named export for discoverability.

---

## 4. Center label

`centerLabel` is the primary DonutChart-specific use case — render a bold total value and a subtitle in the hole.
