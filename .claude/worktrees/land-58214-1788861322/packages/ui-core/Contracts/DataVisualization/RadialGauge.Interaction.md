# RadialGauge — Interaction Contract

- **Component:** RadialGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadialGauge.Semantic.md) · [Accessibility](./RadialGauge.Accessibility.md) · [Styling](./RadialGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A21 RadialGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik RadialGauge baseline)
- **Aliases-canonical-ref:** [CircularGauge](./CircularGauge.Interaction.md) _(link only; update manually when canonical changes)_

---

> **Display-only component.** This Interaction contract is intentionally minimal. Gauge components have no user-initiated interactions — they are read-only display components. All runtime value changes are driven by prop updates from the host. This contract records animation timing and known gaps only; the thinness is by design.

## 1. Interaction

Extends CircularGauge — see `CircularGauge.Interaction.md` for base interaction. RadialGauge adds tick-mark animation timing below.

Needle rotation animation: 600ms ease-in-out on value change.

---

## 2. Known gaps

None beyond CircularGauge gaps.
