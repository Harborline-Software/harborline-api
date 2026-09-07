# HeatmapChart — Accessibility Contract

- **Component:** HeatmapChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./HeatmapChart.Semantic.md) · [Interaction](./HeatmapChart.Interaction.md) · [Styling](./HeatmapChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A10 HeatmapChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Root element

`<figure role="img" aria-label="Heatmap: {yLabels[0]}–{yLabels[n]} by {xLabels[0]}–{xLabels[n]}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with x labels as column headers and y labels as row headers.

---

## 3. SVG / grid

`aria-hidden="true"` on the SVG. Color is the sole encoding channel — critical data must also be available via tooltip text and the data-table fallback.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-HEAT-A1 | High | Color-only encoding fails WCAG 1.4.1 (Use of Color) when used without numeric labels in cells | Accepted-risk M1; consumers must enable `label` or provide data-table fallback; add numeric cell labels option in M2 |
| G-HEAT-A2 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
