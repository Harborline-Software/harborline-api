# StockChart — Accessibility Contract

- **Component:** StockChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./StockChart.Semantic.md) · [Interaction](./StockChart.Interaction.md) · [Styling](./StockChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 StockChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik StockChart baseline)

---

## 1. Root element

`<figure role="img" aria-label="Stock chart: {data length} periods">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Date, Open, High, Low, Close, Volume.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-STOCK-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-STOCK-A2 | Medium | Candlestick color (green up / red down) is a color-only encoding | Accepted-risk M1; OHLC type and tooltip text supply numeric values |
