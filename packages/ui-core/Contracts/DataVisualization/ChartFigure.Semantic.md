# ChartFigure — Semantic Contract

- **Component:** ChartFigure
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **API disposition:** Internal composition — chart-family accessibility chrome used by public
  chart components; intentionally not exported from `@harborline-software/ui-react`
- **Companion contracts:** [Interaction](./ChartFigure.Interaction.md) · [Styling](./ChartFigure.Styling.md) · [Accessibility](./ChartFigure.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartFigure.tsx`

---

## 1. Purpose

`ChartFigure` is the shared chart-accessibility wrapper for the chart family
(L7 / A11y §10). ECharts renders an unlabelled `<canvas>`/`<div>` that carries
no accessible name and is opaque to assistive technology (WCAG 1.1.1 / 4.1.3).
`ChartFigure` wraps that container in a `<figure role="img">` carrying the
accessible name, marks the visual canvas `aria-hidden`, and exposes an optional
visually-hidden (`sr-only`) data summary. A screen reader therefore announces a
single labelled image (plus the optional tabular summary) instead of an
anonymous graphic.

It is a **drop-in replacement for the bare container `<div>`**: it carries
`width` / `height` / `style` / `className` 1:1, so consumers that sized or
styled the chart keep working unchanged. It formalises the precedent already
established by `WaterfallChart` and `SankeyChart` so the rollout across the rest
of the family is mechanical.

The module also exports the helper `chartAriaLabel(ariaLabel, title, fallback)`
which resolves the accessible name by precedence (explicit `ariaLabel` → `title`
→ per-chart `fallback`).

## 2. Data model

```typescript
export interface ChartFigureProps {
  /** Ref wired to the inner ECharts container element (from `useChart`). */
  containerRef: React.Ref<HTMLDivElement>
  /** Accessible name for the chart, announced as the figure's `aria-label`. Derive with `chartAriaLabel`. */
  ariaLabel: string
  width?: string | number   // default '100%'
  height?: string | number  // default 300
  className?: string
  style?: React.CSSProperties
  /** Visually-hidden data summary (e.g. a `<table>` / `<ul>`) exposed only to AT, inside `.sr-only`. */
  summary?: React.ReactNode
  /** In-flow overlay content (center labels, drill-up buttons) — NOT aria-hidden, so it stays reachable. */
  children?: React.ReactNode
}

export function chartAriaLabel(
  ariaLabel: string | undefined,
  title: string | undefined,
  fallback: string,
): string
```

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `containerRef` | `React.Ref<HTMLDivElement>` (required) | — | Wired to the inner ECharts container; the figure mounts the chart here and marks it `aria-hidden`. |
| `ariaLabel` | `string` (required) | — | The figure's accessible name (`aria-label` on `role="img"`). Resolve via `chartAriaLabel`. |
| `width` | `string \| number` | `'100%'` | Outer figure width (replaces the old container width 1:1). |
| `height` | `string \| number` | `300` | Outer figure height. |
| `className` | `string` | — | Applied to the `<figure>`. |
| `style` | `React.CSSProperties` | — | Merged onto the `<figure>` after `width`/`height`. |
| `summary` | `ReactNode` | — | AT-only data summary rendered in an `.sr-only` container after the chart. |
| `children` | `ReactNode` | — | In-flow overlay siblings of the canvas (not `aria-hidden`). |

## 4. Events

None. `ChartFigure` is a presentational/structural wrapper and exposes no
callbacks. Chart interactivity (tooltips, drill, zoom) lives on the ECharts
instance attached via `containerRef`; overlay interactivity lives in `children`.

## 5. Slots

- **`children`** — in-flow overlay content rendered as siblings of the ECharts
  container; **not** `aria-hidden`, so interactive overlays (center labels,
  drill-up buttons) remain reachable.
- **`summary`** — visually-hidden (`sr-only`) data summary exposed only to
  assistive technology.
- The chart canvas itself is **not** a slot — it is wired via `containerRef` and
  rendered `aria-hidden`.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
