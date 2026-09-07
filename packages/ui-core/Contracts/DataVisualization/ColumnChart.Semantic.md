# ColumnChart — Semantic Contract

- **Component:** ColumnChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./ColumnChart.Interaction.md) · [Accessibility](./ColumnChart.Accessibility.md) · [Styling](./ColumnChart.Styling.md)
- **Aliases-canonical-ref:** [BarChart](./BarChart.Semantic.md) _(link only; update manually when canonical changes)_
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart layout=horizontal / Telerik Column Chart baseline)
- **Catalog row:** #A6 ColumnChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**ColumnChart** — renders vertical bars for comparing values across categories. Bars extend bottom-to-top; categories are on the x-axis. Distinct from BarChart (which uses horizontal bars). Equivalent to BarChart with Recharts `layout='horizontal'` (categories on x-axis, bars extend bottom-to-top).

---

## 2. Props (planned)

```typescript
interface ColumnChartProps extends Omit<BarChartProps, 'layout'> {
  // layout is omitted — ColumnChart always uses Recharts layout='horizontal' (vertical bars)
}
```

Inherits all props from `BarChartProps`. See `BarChart.Semantic.md` for the full interface.

---

## 3. Relationship to BarChart

ColumnChart is BarChart with `layout='horizontal'` preset. Exists as a named export for discoverability in line with Telerik naming. No additional behavior.

---

## 4. Grouped vs stacked

Same as BarChart — see [BarChart.Semantic.md §4](./BarChart.Semantic.md).

---

## 5. Chart axis prop family — naming rationale

The chart family uses two cross-axis data shapes that reflect the underlying axis semantics:

| Chart type | Cross-axis prop | Rationale |
|---|---|---|
| Categorical charts (AreaChart, LineChart, BarChart, ColumnChart) | `categoryKey: string` | Cross axis is the independent categorical or time variable; key-based access into a row-shaped `data: ChartDataPoint[]` array |
| Point charts (BubbleChart, ScatterChart) | positional `{x, y}` on each data point | Both axes are numeric/quantitative; no single-key abstraction applies — each point carries its own coordinates |

**PL3-8 disposition:** The categorical-vs-point split is architectural and intentional — point charts have quantitative axes that resist key-based abstraction. Within the categorical family, the prop name is unified on `categoryKey` across AreaChart, LineChart, BarChart, and ColumnChart (no `xDataKey` divergence remains in the current contracts). Historical `xDataKey` shape — if any consumer code still uses it — is a migration target, not a forward contract. Divergence is documented and intentional; no further alignment blocking.
