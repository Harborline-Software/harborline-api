# Sankey — Interaction Contract

- **Component:** Sankey
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sankey.Semantic.md) · [Interaction](./Sankey.Interaction.md) · [Accessibility](./Sankey.Accessibility.md) · [Styling](./Sankey.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Sankey.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts item-trigger tooltip on hover (default `{ trigger: 'item' }`). Shows node name or link source/target and value when hovering over nodes and links respectively. This is distinct from the axis-trigger tooltip used by Cartesian charts.

---

## 2. Node drag

ECharts Sankey nodes support drag-to-reposition by default. The `Sankey` component does not disable this behavior. Users can drag nodes vertically (horizontal layout) to rearrange the diagram. Repositioned node positions are not persisted.

---

## 3. Legend

When `legend` is truthy, ECharts renders a legend. For Sankey, the legend would represent node or link groups, but standard ECharts Sankey does not have a conventional legend by default.

---

## 4. Series click

No custom ECharts click handler is registered. `onSeriesClick` is inherited and forwarded through `buildBaseOption` / `useChart`; node/link click callbacks are not explicitly wired.

---

## 5. No zoom / pan

No zoom or pan is configured. Large Sankey diagrams that exceed the container dimensions may require the consumer to set a larger `height`.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SNKY1 | Medium | Node drag positions are not persisted — layout resets on re-render | Accepted-risk M1; ECharts does not expose node positions as React state; custom persistence requires tapping ECharts instance |
| G-SNKY2 | Low | `tooltip` cast to `never` suppresses TypeScript type safety on the tooltip prop — any consumer-supplied `ChartTooltipProps` is silently overridden | Accepted-risk M1; refine the type to accept raw ECharts tooltip config in M2 |
| G-SNKY3 | Low | No node click callback — consumers cannot react to individual node selection | Accepted-risk M1; add `onNodeClick?: (node: SankeyNode) => void` in M2 |
