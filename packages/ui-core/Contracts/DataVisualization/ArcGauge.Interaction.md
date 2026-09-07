# ArcGauge — Interaction Contract

- **Component:** ArcGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ArcGauge.Semantic.md) · [Accessibility](./ArcGauge.Accessibility.md) · [Styling](./ArcGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A20 ArcGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ArcGauge baseline)

---

> **Display-only component.** This Interaction contract is intentionally minimal. Gauge components have no user-initiated interactions — they are read-only display components. All runtime value changes are driven by prop updates from the host. This contract records animation timing and known gaps only; the thinness is by design.

## 1. Display only

Read-only. Value changes animate arc stroke-dashoffset transition (400ms ease).

---

## 2. Known gaps

None identified for forward-spec.
