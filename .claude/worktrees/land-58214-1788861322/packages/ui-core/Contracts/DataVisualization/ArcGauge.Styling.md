# ArcGauge — Styling Contract

- **Component:** ArcGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ArcGauge.Semantic.md) · [Interaction](./ArcGauge.Interaction.md) · [Accessibility](./ArcGauge.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A20 ArcGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ArcGauge baseline)

---

## 1. Track arc

SVG `<circle>` with `stroke-dasharray` / `stroke-dashoffset` for arc rendering. Track stroke: `trackColor` prop or `hsl(var(--muted))`. `stroke-width`: `strokeWidth` prop (default 12). `stroke-linecap: round`.

---

## 2. Value arc

Same SVG circle, different stroke color: `color` prop or `hsl(var(--primary))`. Animated `stroke-dashoffset` transition 400ms.

---

## 3. Center label

Same as CircularGauge.Styling.md §4.

---

## 4. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`.
