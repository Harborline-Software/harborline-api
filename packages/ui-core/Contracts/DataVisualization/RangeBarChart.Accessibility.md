# RangeBarChart — Accessibility Contract

- **Component:** RangeBarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RangeBarChart.Semantic.md) · [Interaction](./RangeBarChart.Interaction.md) · [Styling](./RangeBarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A17 RangeBarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Range bar chart: {data.length} items">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Category, From, To, Range.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RBAR-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
