# RadialGauge — Styling Contract

- **Component:** RadialGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadialGauge.Semantic.md) · [Interaction](./RadialGauge.Interaction.md) · [Accessibility](./RadialGauge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A21 RadialGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik RadialGauge baseline)
- **Aliases-canonical-ref:** [CircularGauge](./CircularGauge.Styling.md) _(link only; update manually when canonical changes)_

---

## 1. Disk background

`fill: hsl(var(--card))` circle behind needle assembly.

---

## 2. Track arc

Same as CircularGauge.Styling.md §1-2.

---

## 3. Tick marks

Major ticks: `stroke: hsl(var(--foreground))` `stroke-width: 2`, 8px length. Minor ticks: `stroke: hsl(var(--muted-foreground))` `stroke-width: 1`, 4px length.

---

## 4. Scale labels

`text-xs fill-muted-foreground` at major tick positions.

---

## 5. Needle

Triangle SVG path `fill: hsl(var(--foreground))`. Center pivot: small `<circle r="4" fill: hsl(var(--primary))>`.

---

## 6. Center label

Same as CircularGauge.Styling.md §4.

---

## 7. Design tokens

Uses: `hsl(var(--card))`, `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`.
