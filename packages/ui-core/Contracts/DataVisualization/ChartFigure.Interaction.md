# ChartFigure — Interaction Contract

- **Component:** ChartFigure
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Draft
- **Companion contracts:** [Semantic](./ChartFigure.Semantic.md) · [Styling](./ChartFigure.Styling.md) · [Accessibility](./ChartFigure.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartFigure.tsx`

---

## 1. Scope

`ChartFigure` adds **no interaction of its own**. It governs how the chart is
exposed to assistive technology, not how the chart behaves. Chart interactivity
(hover tooltips, click/drill, zoom, brushing) belongs to the ECharts instance
attached via `containerRef`; any in-flow controls (drill-up buttons, toggles)
are passed as `children`. This contract documents that boundary.

## 2. Activation & state

Stateless wrapper. On each render it resolves the accessible name once via
`chartAriaLabel` precedence (explicit `ariaLabel` → `title` → per-chart
`fallback`) and renders:

1. the ECharts container (`ref={containerRef}`, `aria-hidden="true"`),
2. the in-flow `children` overlays, then
3. the optional `sr-only` `summary`.

The assistive-technology path is the figure `aria-label` + the `sr-only`
summary — never the canvas, which is hidden from AT.

## 3. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab | Reaches interactive overlay `children` in DOM order; the `<figure>` and the `aria-hidden` canvas are not focus targets. |
| Enter / Space | Delegated to overlay `children` controls (e.g. drill-up buttons); the figure itself handles no keys. |

## 4. Disabled handling

Not applicable — `ChartFigure` has no `disabled` prop or disabled state. A chart
with no data is the chart component's concern, not the figure wrapper's.

## 5. Interaction-state precedence

Not applicable to the wrapper — it has no hover/focus/active/disabled states.
The only precedence it enforces is **name resolution**: explicit `ariaLabel`
wins over `title`, which wins over the per-chart `fallback` (so every chart
stays labelled even when a consumer supplies neither).

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
