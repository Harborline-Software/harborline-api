# SankeyChart — Styling Contract

- **Component:** SankeyChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SankeyChart.Semantic.md) · [Interaction](./SankeyChart.Interaction.md) · [Accessibility](./SankeyChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A23 SankeyChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; D3-Sankey / Recharts Sankey baseline)

---

## 1. Container

`w-full` + explicit `height` (default 400px — taller than standard charts to accommodate node stacking).

---

## 2. Nodes

`fill: hsl(var(--chart-N))` cycling. `rx="2"` rounded corners. Node width: `nodeWidth` prop (default 24px). Hover: brightness +10%.

---

## 3. Links

`fill: source-node-color` at `opacity-30`. Hover state: `opacity-80`. Non-highlighted state (when a node is hovered): `opacity-10`. Link stroke: none.

---

## 4. Node labels

`text-xs fill-foreground`. Positioned to the right of the node for non-terminal nodes, left of terminal nodes (last column). Labels use SVG `<clipPath>` for truncation: define a `<clipPath id="label-clip-{i}">` per label region and apply `clip-path="url(#label-clip-{i})"` on the `<text>` element. Do NOT use `CSS overflow: hidden` (no-op on SVG text) or `textLength` attribute (distorts glyph spacing, does not truncate). Alternatively, measure text width programmatically and truncate the string with "…" in JS before rendering.

---

## 5. Value labels

Optional: small `text-[10px] fill-muted-foreground` centered in the link path for high-value flows (only when space permits — suppress if link width < 12px).

---

## 6. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--background))`.
