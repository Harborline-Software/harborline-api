# PolarChart — Accessibility Contract

- **Component:** PolarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PolarChart.Semantic.md) · [Interaction](./PolarChart.Interaction.md) · [Styling](./PolarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A22 PolarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; ECharts polar/Recharts RadialBar baseline)

---

## 1. Accessibility

Same pattern as AreaChart — see `AreaChart.Accessibility.md`.

`<figure role="img" aria-label="{title or series names} polar chart">` wrapping SVG (`aria-hidden="true"`) + visually-hidden `<table>` data fallback.

---

## 2. Color

Sectors use `hsl(var(--chart-N))` palette. For `type='rose'` with a single series, distinguish sectors by shade or pattern — pure hue-only differentiation fails WCAG 1.4.1 for colorblind users.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-POLAR-A1 | High | Rose chart sectors may be hue-differentiated only | Accepted-risk M1; data table fallback is primary AT path |
