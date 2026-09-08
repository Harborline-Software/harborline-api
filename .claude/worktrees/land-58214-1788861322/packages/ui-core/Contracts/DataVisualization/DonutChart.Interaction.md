# DonutChart — Interaction Contract

- **Component:** DonutChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./DonutChart.Semantic.md) · [Accessibility](./DonutChart.Accessibility.md) · [Styling](./DonutChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A5 DonutChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)
- **Aliases-canonical-ref:** [PieChart](./PieChart.Interaction.md) _(link only; update manually when canonical changes)_

---

## 1. Interaction

Extends PieChart — see `PieChart.Interaction.md` for base interaction. DonutChart-specific additions follow.

---

## 2. Center label on hover

Optional behavior: when a segment is hovered, the `centerLabel` content may update to reflect the hovered segment's value (controlled by caller via segment hover callback). This is not built-in behavior — callers implement it by wiring the hover state to `centerLabel`.

---

## 3. Known gaps

None beyond PieChart gaps.
