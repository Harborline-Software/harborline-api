# BoxPlotChart — Interaction Contract

- **Component:** BoxPlotChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Draft
- **Companion contracts:** [Semantic](./BoxPlotChart.Semantic.md) · [Styling](./BoxPlotChart.Styling.md) · [Accessibility](./BoxPlotChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/BoxPlotChart.tsx`

---

## 1. Scope

TODO: which interaction behaviors this contract governs for BoxPlotChart.

## 2. Activation & state

TODO: triggers, controlled/uncontrolled lifecycle, callback chain.

## 3. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab | TODO |
| Enter / Space | TODO |

## 4. Disabled handling

TODO.

## 5. Interaction-state precedence

1. Disabled
2. Active / selected
3. Focus / hover
4. Normal

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
