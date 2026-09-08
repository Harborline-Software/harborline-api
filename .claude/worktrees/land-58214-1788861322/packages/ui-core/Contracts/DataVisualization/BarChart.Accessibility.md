# BarChart — Accessibility Contract

- **Component:** BarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BarChart.Semantic.md) · [Interaction](./BarChart.Interaction.md) · [Styling](./BarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart baseline)
- **Catalog row:** #A2 BarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="{categoryKey} comparison">`. Consumers supply a meaningful description.

---

## 2. Data table fallback

Visually-hidden `<table>` with category rows and series columns mirrors chart data for AT.

---

## 3. SVG

`aria-hidden="true"` on the SVG — data-table fallback is the AT-accessible path.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BAR-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1; implement at M2 |
| G-BAR-A2 | Low | Color-only series differentiation | Accepted-risk M1; add pattern fills in M2 |
