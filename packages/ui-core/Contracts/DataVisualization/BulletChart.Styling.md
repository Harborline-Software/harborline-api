# BulletChart — Styling Contract

- **Component:** BulletChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BulletChart.Semantic.md) · [Interaction](./BulletChart.Interaction.md) · [Accessibility](./BulletChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A11 BulletChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Range bands

Adjacent SVG `<rect>` elements. Default colors: light (`hsl(var(--muted))`) → mid (`hsl(var(--muted-foreground)/0.3)`) → dark (`hsl(var(--muted-foreground)/0.5)`) for poor→ok→good. Override with `BulletRange.color`.

---

## 2. Value bar

`fill: hsl(var(--primary))` at 80% height of the band area. Rounded right corners: `rx: 2`.

---

## 3. Target marker

Thin vertical `<rect>` 3px wide, full band height. `fill: hsl(var(--foreground))`.

---

## 4. Title / subtitle

Title: `text-sm font-medium text-foreground`. Subtitle: `text-xs text-muted-foreground`.

---

## 5. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--muted-foreground))`, `hsl(var(--foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
