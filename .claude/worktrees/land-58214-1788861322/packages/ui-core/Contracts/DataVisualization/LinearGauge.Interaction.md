# LinearGauge — Interaction Contract

- **Component:** LinearGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./LinearGauge.Semantic.md) · [Accessibility](./LinearGauge.Accessibility.md) · [Styling](./LinearGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A19 LinearGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik LinearGauge baseline)

---

> **Display-only component.** This Interaction contract is intentionally minimal. Gauge components have no user-initiated interactions — they are read-only display components. All runtime value changes are driven by prop updates from the host. This contract records animation timing and known gaps only; the thinness is by design.

## 1. Display only

Read-only. Value changes animate the fill bar (CSS transition 400ms ease).

---

## 2. Known gaps

None identified for forward-spec.
