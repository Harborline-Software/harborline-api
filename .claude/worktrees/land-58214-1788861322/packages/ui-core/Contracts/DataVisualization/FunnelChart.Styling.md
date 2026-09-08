# FunnelChart — Styling Contract

- **Component:** FunnelChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./FunnelChart.Semantic.md) · [Interaction](./FunnelChart.Interaction.md) · [Accessibility](./FunnelChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 FunnelChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Stages

Trapezoid SVG shapes. Fill: `hsl(var(--chart-N))` in sequence or `FunnelStage.color` override. Hover: `filter: brightness(1.1)`.

Stage-to-stage connectors: small triangular SVG wedge fill `hsl(var(--muted))` between adjacent stages.

---

## 2. Labels

Stage name: `text-sm font-medium text-foreground`. Value / percent: `text-xs text-muted-foreground`.

---

## 3. Tooltip

Same as AreaChart.Styling.md §6.

---

## 4. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
