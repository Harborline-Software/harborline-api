# Sparkline — Styling Contract

- **Component:** Sparkline
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sparkline.Semantic.md) · [Interaction](./Sparkline.Interaction.md) · [Accessibility](./Sparkline.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Sparkline baseline)
- **Catalog row:** #120 Sparkline (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Sparkline baseline)

---

## 1. Root container

`inline-block w-full` (or fixed-width via `width` prop). No border or background — renders directly in the parent cell/card.

---

## 2. SVG canvas

`<svg width="100%" height="{height}">`. Overflow hidden via `viewBox`. No padding applied to SVG itself; caller manages surrounding spacing.

---

## 3. Line / Area

Stroke: `color` prop or `hsl(var(--primary))` default. `stroke-width`: `lineWidth` prop (default 1.5px).

Area fill: same `color` at `opacity: 0.15`.

---

## 4. Bar

Fill: `color` prop or `hsl(var(--primary))` for positive values. Negative bars: `negativeColor` prop or `hsl(var(--destructive))`.

Bar gap: 1px between bars.

---

## 5. Markers

`<circle>` at each data point. Radius: `markerSize` prop (default 3px). Fill: `color` prop or `hsl(var(--primary))`. Stroke: `hsl(var(--background))` 1px (for contrast against area fill).

---

## 6. Tooltip

`absolute` positioned `div` above hovered point. `text-xs bg-popover text-popover-foreground rounded shadow px-2 py-1 pointer-events-none z-50`.

---

## 7. Design tokens

Uses: `hsl(var(--primary))`, `hsl(var(--destructive))`, `hsl(var(--background))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
