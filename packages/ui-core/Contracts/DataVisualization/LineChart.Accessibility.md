# LineChart — Accessibility Contract

- **Component:** LineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./LineChart.Semantic.md) · [Interaction](./LineChart.Interaction.md) · [Styling](./LineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts LineChart baseline)
- **Catalog row:** #A3 LineChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="{xAxisLabel} trend">`. Consumers supply meaningful description.

---

## 2. Data table fallback

Visually-hidden `<table>` inside `<figure>` for AT navigation.

---

## 3. SVG

`aria-hidden="true"` — data-table is the AT path.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LINE-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-LINE-A2 | Low | Color-only series differentiation; dashed `strokeDasharray` partially mitigates | Accepted-risk M1 |
