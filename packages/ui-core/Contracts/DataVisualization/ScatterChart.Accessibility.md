# ScatterChart — Accessibility Contract

- **Component:** ScatterChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ScatterChart.Semantic.md) · [Interaction](./ScatterChart.Interaction.md) · [Styling](./ScatterChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 ScatterChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts baseline)

---

## 1. Root element

`<figure role="img" aria-label="{xAxisLabel} vs {yAxisLabel} scatter plot">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns per series: Label (if present), X, Y.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Symbol differentiation

`symbol` prop provides shape-based series differentiation as a supplement to color (aids colorblind users).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SCAT-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
