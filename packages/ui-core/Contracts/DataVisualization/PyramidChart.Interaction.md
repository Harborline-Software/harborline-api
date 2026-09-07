# PyramidChart — Interaction Contract

- **Component:** PyramidChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PyramidChart.Semantic.md) · [Interaction](./PyramidChart.Interaction.md) · [Accessibility](./PyramidChart.Accessibility.md) · [Styling](./PyramidChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/PyramidChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover tooltip

ECharts default tooltip on hover when `tooltip` is truthy. Shows segment name and value for the hovered pyramid tier.

---

## 2. Segment click (`onItemClick`)

When `onItemClick` is provided, a ECharts `click` event handler is registered via `inst.on('click', handler)`. On click:
1. The handler receives `params.data.name` and `params.data.value` from ECharts.
2. The matching `FunnelDataItem` is located in `data` by `name`.
3. `onItemClick(item)` fires with the full `FunnelDataItem` including any `color` field.

If no matching item is found in `data`, the callback does not fire.

The handler is cleaned up via `inst.off('click', handler)` when `instanceRef`, `onItemClick`, or `data` changes.

---

## 3. Legend

When `legend` is truthy, ECharts renders a legend with one entry per data item. Clicking a legend entry toggles that segment's visibility (standard ECharts behavior).

---

## 4. No selection state

There is no persistent selection state. Clicking a segment fires the callback but does not visually select or highlight the segment beyond ECharts' default click emphasis.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PYR1 | Low | No `direction`, `orient`, or `gap` controls — pyramid is always vertical with default gap | Accepted-risk M1; expose these props if horizontal/gapped pyramid variants are needed |
| G-PYR2 | Low | No persistent selected-segment state | Accepted-risk M1; callers manage selection externally via `onItemClick` |
