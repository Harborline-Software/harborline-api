# PieChart — Accessibility Contract

- **Component:** PieChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PieChart.Semantic.md) · [Interaction](./PieChart.Interaction.md) · [Styling](./PieChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A4 PieChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="Breakdown by {first segment name or consumer-supplied label}">`.

---

## 2. Data table fallback

Visually-hidden `<table>` with columns: Name, Value, Percentage.

---

## 3. SVG

`aria-hidden="true"` — data-table is the AT path.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PIE-A1 | Medium | Data-table fallback is forward-spec only | Accepted-risk M1 |
| G-PIE-A2 | Low | Small segments may have very thin wedges — hard to perceive for low-vision users | Accepted-risk M1; `paddingAngle` and `minSliceAngle` can mitigate at call site |
