# CircularGauge — Styling Contract

- **Component:** CircularGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./CircularGauge.Semantic.md) · [Interaction](./CircularGauge.Interaction.md) · [Accessibility](./CircularGauge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A18 CircularGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik CircularGauge baseline)

---

## 1. Track arc

`stroke: hsl(var(--muted))` `stroke-width: 8` on a circular arc path.

---

## 2. Value arc / needle

Value arc fill: `hsl(var(--primary))`. Range-band overrides via `ranges[].color`. Needle: `fill: hsl(var(--foreground))` thin triangle.

---

## 3. Scale labels

`text-xs fill-muted-foreground` positioned around the arc.

---

## 4. Center label

Centered `div` overlay: primary value `text-2xl font-bold text-foreground`, unit/sub-label `text-xs text-muted-foreground`.

---

## 5. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`.
