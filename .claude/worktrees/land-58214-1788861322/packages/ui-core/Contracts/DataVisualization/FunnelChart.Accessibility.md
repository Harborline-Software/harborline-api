# FunnelChart — Accessibility Contract

- **Component:** FunnelChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./FunnelChart.Semantic.md) · [Interaction](./FunnelChart.Interaction.md) · [Styling](./FunnelChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 FunnelChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Funnel: {data[0].name} through {data[n-1].name}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Stage, Value, Conversion %.

---

## 3. SVG

`aria-hidden="true"`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FUNNEL-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
