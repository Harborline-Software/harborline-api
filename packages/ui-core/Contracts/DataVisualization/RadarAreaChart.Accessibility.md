# RadarAreaChart — Accessibility Contract

- **Component:** RadarAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadarAreaChart.Semantic.md) · [Interaction](./RadarAreaChart.Interaction.md) · [Styling](./RadarAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A13 RadarAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Radar comparison: {series names}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with subject rows and series columns.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RADAR-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-RADAR-A2 | Low | Polar geometry is harder to interpret for low-vision users than Cartesian | Accepted-risk M1; data-table fallback is the AT-accessible path |
