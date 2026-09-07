# ChartFigure — Accessibility Contract

- **Component:** ChartFigure
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Draft
- **Companion contracts:** [Semantic](./ChartFigure.Semantic.md) · [Interaction](./ChartFigure.Interaction.md) · [Styling](./ChartFigure.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartFigure.tsx`

---

## 1. Purpose

`ChartFigure` **is** the chart family's accessibility primitive. ECharts renders
an unlabelled `<canvas>`/`<div>` that carries no accessible name and is opaque
to assistive technology — a WCAG 2.2 SC 1.1.1 (Non-text Content) violation in
the raw. `ChartFigure` resolves it: it wraps the chart in a
`<figure role="img">` carrying the accessible name, marks the visual container
`aria-hidden`, and exposes an optional visually-hidden (`sr-only`) data summary.
A screen reader therefore announces **one labelled image** (plus the optional
tabular summary) instead of an anonymous graphic.

This formalises the precedent established by `WaterfallChart` and `SankeyChart`
([WaterfallChart.Accessibility §1–§3](./WaterfallChart.Accessibility.md)) so the
rollout across the rest of the family is mechanical. Every requirement is keyed
to a WCAG 2.2 AA success criterion.

---

## 2. Roles & ARIA wiring

| Element | Role / attribute | Notes |
|---|---|---|
| `<figure>` (root) | `role="img"` + `aria-label={ariaLabel}` | The single accessible name for the whole chart; `role="img"` collapses the subtree to one labelled image |
| inner ECharts container `<div>` | `aria-hidden="true"` | The visual canvas is removed from the AT tree — the anonymous graphic is never announced |
| `summary` slot | wrapped in `.sr-only` | AT-only data summary (e.g. a `<table>` / `<ul>`); present in the AT tree, hidden visually — the text-alternative path |
| `children` overlays | NOT `aria-hidden` | In-flow interactive overlays (center labels, drill-up buttons) remain reachable to AT and keyboard |

**WCAG citations:** SC 1.1.1 Non-text Content (the figure label + `sr-only`
summary are the text alternative for the canvas); SC 4.1.2 Name, Role, Value
(`role="img"` + `aria-label`).

---

## 3. Accessible-name resolution

The accessible name is resolved by the exported helper
`chartAriaLabel(ariaLabel, title, fallback)` with strict precedence:

1. explicit `ariaLabel` (trimmed, if non-empty), else
2. `title` (trimmed, if non-empty), else
3. a per-chart `fallback` string.

The `fallback` guarantees **every** chart stays labelled even when a consumer
supplies neither an explicit label nor a title — there is no unlabelled-chart
path. `ariaLabel` is a **required** prop on `ChartFigure` (Semantic §3); the
helper exists so each chart component derives a meaningful default rather than
shipping an empty string.

**WCAG citation:** SC 4.1.2 Name, Role, Value.

---

## 4. Data summary (the AT path)

The assistive-technology path is the figure `aria-label` **plus** the optional
`sr-only` `summary` — never the canvas. For data-bearing charts, supplying a
real tabular `summary` (e.g. a `<table>` of categories/values, as
[WaterfallChart.Accessibility §2](./WaterfallChart.Accessibility.md) prescribes:
Item / Delta / Running Total) is STRONGLY recommended — the `aria-label` alone
conveys *what the chart is*, while the summary conveys *the data*. When omitted,
the chart degrades to label-only (see Known gaps G-CF1).

**WCAG citation:** SC 1.1.1 Non-text Content.

---

## 5. Keyboard model

| Key | Behaviour |
|---|---|
| Tab | Reaches interactive overlay `children` in DOM order; the `<figure>` and the `aria-hidden` canvas are **not** focus targets |
| Enter / Space | Delegated to overlay `children` controls (e.g. drill-up buttons); the figure handles no keys |

`ChartFigure` adds no keyboard behaviour of its own. The chart's native
interactivity (hover tooltips, click-drill, zoom, brushing) lives on the ECharts
instance and is **pointer-only** on the `aria-hidden` canvas — keyboard/AT users
cannot reach per-datum detail through the canvas. The `sr-only` summary is the
equivalent-access channel for that data (see §4 and Known gaps G-CF2).

**WCAG citation:** SC 2.1.1 Keyboard.

---

## 6. Focus management

None. `ChartFigure` is not focusable and never moves focus on mount/unmount or
on data change. Interactive overlay `children` manage their own focus.

**WCAG citation:** SC 3.2.2 On Input — the wrapper causes no context change.

---

## 7. Colour & contrast

`ChartFigure` paints nothing, so it owns no contrast obligation directly.
Series-colour contrast and **colour-independence** (not encoding meaning by
colour alone) are the chart layer's responsibility — e.g. WaterfallChart's
sign-bearing bar labels ([WaterfallChart.Accessibility §4, G-WFALL-A2](./WaterfallChart.Accessibility.md))
and the Chart palette ([Chart.Styling](./Chart.Styling.md)). `ChartFigure`'s
contribution to colour-independence is structural: by guaranteeing a
non-colour channel (the `aria-label` + `sr-only` summary), the data remains
available even to users who cannot perceive the series colours.

**WCAG citations:** SC 1.4.1 Use of Color; SC 1.4.11 Non-text Contrast — both
discharged at the chart/theme layer, not the wrapper.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CF1 | Medium | The `sr-only` data summary is OPTIONAL; when a consumer omits it, the AT path is the `aria-label` alone (no granular data) | Accepted-risk; a tabular summary SHOULD be supplied for data-bearing charts (§4) |
| G-CF2 | Medium | Chart interactivity (tooltip / drill / zoom) is pointer-only on the `aria-hidden` canvas — keyboard/AT users cannot access per-datum detail through the canvas | Mitigated by the `sr-only` summary; full keyboard-navigable data exploration is a future-wave enhancement |
| G-CF3 | Low | `ariaLabel` is a required prop but an empty string is not rejected at runtime; `chartAriaLabel` then falls through to `title` → `fallback`, so the chart still ends up labelled | Accepted — the fallback chain is the safety net; no unlabelled path exists |

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
