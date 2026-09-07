# Loader — Styling Contract

- **Component:** Loader
- **ADR 0017 family:** Feedback
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Loader.Semantic.md) · [Interaction](./Loader.Interaction.md) · [Accessibility](./Loader.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec_
- **Catalog row:** #79 Loader (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Loader communicates that an asynchronous operation is in flight. It is the
visual companion to the `aria-busy` attribute (Accessibility §2) and to
the broader skeleton-loading pattern used in DataGrid / Card list contexts.

This contract names the `--sf-loader-*` token surface (variant, size,
colour, motion) and pins the Tailwind class recipes for the M2 default
layer. Loader is intentionally minimal — it's a small, focused primitive
without business logic.

---

## 2. Token surface

Loader exposes the following CSS custom properties (the `--sf-loader-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Variant axis

Three visual variants. Engineer chooses per surface.

| Variant | Visual | Use case |
|---|---|---|
| `spinner` (default) | Rotating arc / circle | Default for in-button loading, inline content "fetching" |
| `dots` | Three dots that fade in-and-out in sequence | Compact text-flow loading |
| `bar` | Linear horizontal progress bar (indeterminate) | Top-of-page or section-load progress |

### 2.2 Size axis

Four sizes.

| Size | Spinner / dots diameter | Bar height |
|---|---|---|
| `xs` | `0.75rem` (12px) | `1.5px` |
| `sm` | `1rem` (16px) | `2px` |
| `md` (default) | `1.5rem` (24px) | `3px` |
| `lg` | `2rem` (32px) | `4px` |

### 2.3 Token table

| Token | Semantic role | Varies | Notes |
|---|---|---|---|
| `--sf-loader-color` | Loader foreground colour (spinner arc, dots, bar fill) | none (shared) | Resolves to `--sf-color-primary` (brand-accent) by default; consumer override common for "white spinner on primary button" pattern |
| `--sf-loader-track-color` | Track / background colour (the un-filled portion of the arc or bar) | none | Typically `--sf-color-neutral-200`; for transparent buttons resolves to `currentColor` at low opacity |
| `--sf-loader-size-{size}` | Loader dimensions (height + width for spinner; height-only for bar) | size | per §2.2 |
| `--sf-loader-stroke-width` | Spinner arc stroke width | none | typically `1.5px` for xs, `2px` for sm, `3px` for md, `4px` for lg — derives from size |
| `--sf-loader-bar-radius` | Bar variant corner radius | none | typically `9999px` (full pill) so the indeterminate sliding segment has rounded ends |
| `--sf-loader-animation-duration` | Animation cycle duration | none | typically `1s` for spinner, `1.4s` for dots, `1.5s` for bar |

### 2.4 Token additions to `feedback.tokens.json`

The `--sf-loader-*` family extends `feedback.tokens.json`. Recommended
namespace addition:

```jsonc
"loader": {
  "_intent": "three variants × four sizes; brand-accent default; reduced-motion compliant. Pairs with aria-busy on the containing region (Accessibility §2)",
  "color": "#2563EB",
  "trackColor": "#E5E7EB",
  "size": {
    "xs": { "dimension": "0.75rem", "strokeWidth": "1.5px", "barHeight": "1.5px" },
    "sm": { "dimension": "1rem",   "strokeWidth": "2px", "barHeight": "2px" },
    "md": { "dimension": "1.5rem", "strokeWidth": "3px", "barHeight": "3px" },
    "lg": { "dimension": "2rem",   "strokeWidth": "4px", "barHeight": "4px" }
  },
  "barRadius": "9999px",
  "animationDuration": {
    "spinner": "1s",
    "dots": "1.4s",
    "bar": "1.5s"
  },
  "_notes": {
    "contrast": "Loader color on its track and on the surrounding surface MUST present a perceivable colour shift. Per WCAG 2.2 SC 1.4.11, a 3:1 contrast against the surrounding bg is required for non-text indicators. Default tokens satisfy this on light theme; provider overrides MUST re-verify.",
    "reducedMotion": "All animations MUST honor prefers-reduced-motion: reduce. Replace continuous animation with a static glyph (e.g., a static dot/three-dots pattern, or a 'loading...' text indicator). The progress channel is then announced via aria-live alongside aria-busy.",
    "inheritColor": "When Loader is composed inside a button or coloured surface (e.g., a primary-button spinner), set color to inherit (`currentColor`) so the spinner takes the parent's foreground. The token surface supports any override.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Spinner variant

Two acceptable implementations: pure-CSS `animate-spin` + a partial-circle
SVG (Lucide `loader-2`), OR `<svg>` with a stroked arc + dashoffset
animation.

```jsx
{/* Lucide-based (recommended; simpler) */}
<svg role="status" aria-label="Loading" class="h-6 w-6 animate-spin motion-reduce:animate-none text-blue-600" viewBox="0 0 24 24" fill="none">
  <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="3" />
  <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
</svg>
```

### 3.2 Dots variant

```jsx
<span role="status" aria-label="Loading" class="inline-flex items-center gap-1">
  <span class="h-2 w-2 rounded-full bg-blue-600 animate-pulse motion-reduce:animate-none" style="animation-delay: 0ms"></span>
  <span class="h-2 w-2 rounded-full bg-blue-600 animate-pulse motion-reduce:animate-none" style="animation-delay: 200ms"></span>
  <span class="h-2 w-2 rounded-full bg-blue-600 animate-pulse motion-reduce:animate-none" style="animation-delay: 400ms"></span>
</span>
```

### 3.3 Bar variant (indeterminate)

```jsx
<div role="progressbar" aria-label="Loading" aria-valuetext="In progress" class="relative h-1 w-full overflow-hidden rounded-full bg-gray-200">
  <div class="absolute inset-y-0 left-0 w-1/3 bg-blue-600 animate-[loaderbar_1.5s_linear_infinite] motion-reduce:animate-none"></div>
</div>
{/* @keyframes loaderbar { 0% { transform: translateX(-100%); } 100% { transform: translateX(300%); } } */}
```

### 3.4 Size recipes

| Size | Spinner | Dots | Bar |
|---|---|---|---|
| `xs` | `h-3 w-3` | dot `h-1 w-1` | `h-px` |
| `sm` | `h-4 w-4` | dot `h-1.5 w-1.5` | `h-0.5` |
| `md` | `h-6 w-6` | dot `h-2 w-2` | `h-1` |
| `lg` | `h-8 w-8` | dot `h-2.5 w-2.5` | `h-1.5` |

### 3.5 Inline-with-button recipe

When Loader is the leading "icon" inside a Button during loading:

```jsx
<Button variant="primary" loading>
  <Loader variant="spinner" size="sm" class="text-current" />
  Save
</Button>
```

The `text-current` makes the spinner inherit the button's foreground colour
(white on `primary`, brand-accent on `tertiary`).

### 3.6 Full-region loading recipe

Wrap the loading content surface with `aria-busy="true"` + centred Loader:

```jsx
<div role="status" aria-busy="true" aria-label="Loading content" class="flex items-center justify-center py-12">
  <Loader variant="spinner" size="md" />
  <span class="sr-only">Loading…</span>
</div>
```

### 3.7 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer.

---

## 4. Visual state inventory

Loader has a single visual state per variant — it is in motion (or a
static substitute under reduced-motion). No hover / focus / active /
disabled.

| State | Trigger | Notes |
|---|---|---|
| **animating** | default | Continuous animation per `--sf-loader-animation-duration` |
| **reduced-motion** | `prefers-reduced-motion: reduce` | Animation suppressed; static indicator with `aria-busy="true"` (or `aria-live="polite"` + "Loading…" text) carries the signal |

---

## 5. Responsive behaviour

Loader does not respond to viewport. Four sizes are author-chosen.

In a button context, Loader size matches the button's icon-size token:
button `sm` → loader `sm`; button `md` → loader `sm` (the spinner is
smaller than the label area); button `lg` → loader `md`.

---

## 6. Reduced motion

ALL Loader animations MUST honor `prefers-reduced-motion: reduce`:

- Spinner: replace `animate-spin` with a static glyph; SR still gets
  `aria-busy` + `aria-label="Loading"`.
- Dots: replace `animate-pulse` with static dots.
- Bar: replace the sliding-segment animation with a static `w-full
  opacity-50` (indicating "in progress" via a non-animated indicator)
  OR replace with a text indicator "Loading…".

The Accessibility contract documents the SR channel; this contract
documents the visual fallback.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Determinate progress bar.** This contract scopes Loader to
   INDETERMINATE only. A determinate progress bar (with known
   percentage) is a separate primitive (`Progress`); not in M2.
2. **Skeleton loading variant.** Skeleton (placeholder content rectangles
   pulsing) is a distinct primitive. DataGrid already implements its own
   skeleton internally; a global `Skeleton` component is M3.
3. **Animation curve choice.** Defaults to `linear` for the bar (uniform
   sliding) and ease-in-out for spinner / dots. Both are conventional;
   council may tune.
4. **Inline-text dots.** A "Loading…" + trailing dots-that-grow pattern
   exists (`Loading`, `Loading.`, `Loading..`, `Loading...`). The dots
   variant in this contract is the iconographic version; the text version
   is a consumer-composed pattern, not a Loader variant.

---

## 8. Do / Don't

### Do

- Always pair Loader with `aria-busy="true"` on the containing region
  AND/OR `role="status"` on the Loader itself.
- Use the `sm` size inside buttons; `md` for full-region loaders.
- Honor `prefers-reduced-motion` — replace animation with a static
  indicator + SR-readable "Loading…" text.
- Use `currentColor` so the spinner takes its host's foreground colour
  in button / coloured-surface contexts.

### Don't

- Don't put hex values in component code. Component CSS consumes
  `var(--sf-loader-*)` only.
- Don't display Loader for sub-300ms operations — the appear-disappear
  flash is more disruptive than no indicator at all. Defer the loader
  with a 300-500ms threshold (Interaction contract).
- Don't show Loader without an accompanying `aria-busy` or
  `role="status"` — SR users have no signal.
- Don't animate the Loader's container (e.g., a "pulsing wrapper")
  IN ADDITION to the loader itself — the duplicate motion is
  disorienting.
- Don't drop the static fallback under reduced motion. Some affordance
  must remain visible.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** any CSS-animated SVG or DIV
  composition; Lucide `loader-2` is a convenient base for the spinner.
- **Web Components (Phase M4, Lit):** straightforward custom element.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #79 Loader (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- [Loader.Semantic.md](./Loader.Semantic.md) — prop contract
- [Loader.Interaction.md](./Loader.Interaction.md) — defer-timing, behavioural contract
- [Loader.Accessibility.md](./Loader.Accessibility.md) — role=status / aria-busy wiring
- `_shared/design/tokens/feedback.tokens.json` — default token values
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 4.1.3 Status Messages
