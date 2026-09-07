# ChartFigure — Styling Contract

- **Component:** ChartFigure
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Draft
- **Companion contracts:** [Semantic](./ChartFigure.Semantic.md) · [Interaction](./ChartFigure.Interaction.md) · [Accessibility](./ChartFigure.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartFigure.tsx`

---

## 1. Purpose

`ChartFigure` is the shared chart-accessibility **wrapper** — a `<figure>` that
carries box-model sizing and hosts the (visually opaque) ECharts container plus
an optional `sr-only` summary. It paints **nothing**: no background, border,
typography, or series colour. Its token surface is therefore limited to the
**outer sizing box** it owns (`width` / `height`) and the **`sr-only` summary**
container; everything visible — axes, series, tooltip, legend, palette — is
painted by the ECharts theme layer and is governed by [Chart.Styling](./Chart.Styling.md),
not here.

Because the `<figure>` replaces the old bare container `<div>` **1:1**
(carrying `width`/`height`/`style`/`className` verbatim), no chart re-sizes or
re-styles when it adopts the wrapper.

---

## 2. Token surface

`ChartFigure` exposes the `--sf-chart-figure-*` family — sizing tokens only.

| Token | Semantic role | Default | Varies | Notes |
|---|---|---|---|---|
| `--sf-chart-figure-width` | Outer `<figure>` width | `100%` | by prop | Maps 1:1 from the `width` prop onto the figure's inline `style`; replaces the old container width |
| `--sf-chart-figure-height` | Outer `<figure>` height | `300` (px) | by prop | Maps 1:1 from the `height` prop; the inner ECharts container fills it (`width:100%; height:100%`) |

**No paint tokens.** `ChartFigure` declares no background/border/foreground
token. The figure is transparent; the chart's visual surface is the inner
canvas. Consumers needing a card frame around a chart compose that **outside**
`ChartFigure` (or via the host's `className`/`style`), so the wrapper stays a
pure sizing + accessibility seam.

---

## 3. Layout recipe

| Region | Recipe |
|---|---|
| `<figure role="img">` (outer) | `className` applied via `cn(className)`; inline `style={{ width, height, ...style }}` (prop sizing first, host `style` overrides) |
| inner ECharts container `<div>` | `style={{ width: '100%', height: '100%' }}` — fills the figure; `aria-hidden` (see Accessibility §2) |
| `summary` container | `.sr-only` (rendered only when `summary != null`) |
| `children` overlays | in-flow siblings of the canvas; no wrapper styling imposed |

The figure adds **no** default margin, padding, or `display` override — it
inherits the document's block flow, exactly as the bare container did.

---

## 4. The `sr-only` summary

The optional AT-only data summary is wrapped in the fleet `.sr-only` utility —
visually hidden but present in the accessibility tree and the layout's
text-alternative path:

```
position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0;
```

`.sr-only` is a shared design-system utility (Tailwind), not a `--sf-chart-figure-*`
token; it MUST NOT be replaced with `display:none` (which removes the summary
from AT) or `visibility:hidden`.

---

## 5. Visual state inventory

`ChartFigure` has **no visual states** — no hover, focus, active, or disabled
treatment of its own.

| State | Trigger | Recipe |
|---|---|---|
| **default** | always | Transparent figure at the prop-driven `width`/`height` (single state) |
| hover / focus / active | — | NOT owned — chart interactivity (tooltip/hover) is the ECharts instance's, on the `aria-hidden` canvas |
| disabled / empty | — | NOT owned — an empty/no-data chart is the chart component's concern, not the figure's |

---

## 6. Responsive, RTL, reduced motion

- **Responsive:** `width` defaults to `100%`, so the figure is fluid by default;
  `height` is author-chosen. The inner canvas tracks the figure via `100%/100%`;
  ECharts handles its own resize.
- **RTL:** direction-agnostic — the figure carries no directional padding or
  inset. Series/axis mirroring under `dir="rtl"` is an ECharts-theme concern
  (Chart.Styling), not the wrapper's.
- **Reduced motion:** the wrapper has no animation. Chart entrance/transition
  animation lives in the ECharts theme and MUST honour
  `prefers-reduced-motion: reduce` there (WCAG 2.2 SC 2.3.3) — out of scope for
  this wrapper.

---

## 7. Token notes

- Series colours come from per-series `s.color` or the ECharts default/`palette`
  ([Chart.Styling §2](./Chart.Styling.md)); design-token-aligned colour is
  achieved by passing a `palette` resolved from CSS variables, **not** by any
  `--sf-chart-figure-*` token.
- The figure's accessible name (`aria-label`) is a Semantic/Accessibility
  concern, not a style token — see [Accessibility §3](./ChartFigure.Accessibility.md).

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
