# ChartWizard — Interaction Contract

- **Component:** ChartWizard
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ChartWizard.Semantic.md) · [Interaction](./ChartWizard.Interaction.md) · [Accessibility](./ChartWizard.Accessibility.md) · [Styling](./ChartWizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartWizard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Chart type selection

The toolbar renders one `<button>` per allowed chart type. Clicking a button sets `activeType` in local React state, which causes `Chart` to re-render with the new `type` prop. The previously active chart is unmounted and a new ECharts instance initializes with the selected type.

---

## 2. Active type visual feedback

The active button receives `bg-primary text-primary-foreground` classes. Inactive buttons receive `bg-muted text-muted-foreground hover:bg-muted/80`. There is no transition animation between active states.

---

## 3. Chart interactions (delegated to Chart)

All hover tooltip, legend click, and series click behaviors within the rendered chart are delegated to the `Chart` component. See `Chart.Interaction.md` for details. No `ChartBaseProps` event props are forwarded through `ChartWizard` — `onSeriesClick`, `onRender`, `title`, `legend`, `tooltip`, etc. are not available.

---

## 4. No type-change callback

There is no `onTypeChange` prop. The parent cannot observe type changes or control the active type after mount.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-WIZ1 | Medium | Active type is uncontrolled — parent cannot set or observe type selection | Accepted-risk M1; add `value`/`onValueChange` controlled variant in M2 |
| G-WIZ2 | Medium | `title`, `legend`, `tooltip`, `palette`, `onSeriesClick` not forwarded to `Chart` — consumers cannot customize these | Accepted-risk M1; pass through full `Partial<ChartBaseProps>` in M2 |
| G-WIZ3 | Low | Exotic types (`bubble`, `heatmap`, `radar`, `sankey`) listed in `allowedTypes` would be silently ignored | Accepted-risk M1; document allowed set or expand toolbar in M2 |
