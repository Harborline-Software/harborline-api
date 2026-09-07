# PolarChart — Interaction Contract

- **Component:** PolarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PolarChart.Semantic.md) · [Accessibility](./PolarChart.Accessibility.md) · [Styling](./PolarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A22 PolarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; ECharts polar/Recharts RadialBar baseline)

---

## 1. Hover tooltip

Hovering a sector/bar shows a tooltip with the `angleDataKey` label and all series values for that data point. Same tooltip pattern as AreaChart — shared tooltip component.

---

## 2. Legend interaction

When `legend=true`, clicking a legend item toggles that series' visibility (opacity 0 with pointer-events none). Same pattern as AreaChart/BarChart.

---

## 3. Animation

On mount and data change: sectors/bars animate from zero radius outward over 400ms ease-out.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-POLAR1 | Medium | No keyboard navigation between sectors | Accepted-risk M1; read-only chart; AT uses data table fallback |
