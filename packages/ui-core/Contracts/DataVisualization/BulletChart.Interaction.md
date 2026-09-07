# BulletChart — Interaction Contract

- **Component:** BulletChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BulletChart.Semantic.md) · [Accessibility](./BulletChart.Accessibility.md) · [Styling](./BulletChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A11 BulletChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Hover tooltip

When `tooltip=true`: hovering the chart shows value, target, and which range band the value falls in.

---

## 2. No click / selection

Read-only in v1.

---

## 3. Known gaps

None identified for forward-spec.
