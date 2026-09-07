# WaterfallChart — Accessibility Contract

- **Component:** WaterfallChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WaterfallChart.Semantic.md) · [Interaction](./WaterfallChart.Interaction.md) · [Styling](./WaterfallChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 WaterfallChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Waterfall: {data[0].name} to {data[last].name}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Item, Delta, Running Total.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-WFALL-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-WFALL-A2 | Low | Color-only differentiation (increase=green, decrease=red) — red/green colorblind users affected | Accepted-risk M1; bar labels show sign (+/-) as a text supplement |
