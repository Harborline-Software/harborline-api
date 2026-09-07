# FunnelChart — Interaction Contract

- **Component:** FunnelChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./FunnelChart.Semantic.md) · [Accessibility](./FunnelChart.Accessibility.md) · [Styling](./FunnelChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 FunnelChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover

Hovering a stage highlights it (brightness increase) and shows a tooltip with stage name, value, and conversion % from the previous stage.

---

## 2. No click / selection

Read-only in v1.

---

## 3. Known gaps

None identified for forward-spec.
