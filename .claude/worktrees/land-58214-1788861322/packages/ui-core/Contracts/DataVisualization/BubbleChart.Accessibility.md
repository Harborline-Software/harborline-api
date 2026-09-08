# BubbleChart — Accessibility Contract

- **Component:** BubbleChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BubbleChart.Semantic.md) · [Interaction](./BubbleChart.Interaction.md) · [Styling](./BubbleChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A7 BubbleChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="{xAxisLabel} vs {yAxisLabel} by {zLabel}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Label, X, Y, Z for each data point.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BUBBLE-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-BUBBLE-A2 | Medium | Size is a third encoding channel — not describable via color alone; requires label or tooltip text | Accepted-risk M1; data-table fallback provides numeric z values |
