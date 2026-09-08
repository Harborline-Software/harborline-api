# RangeAreaChart — Accessibility Contract

- **Component:** RangeAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RangeAreaChart.Semantic.md) · [Interaction](./RangeAreaChart.Interaction.md) · [Styling](./RangeAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 RangeAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Range chart: {series[0].name}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: X, Low, High, Range.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RAREA-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
