# Loader — Semantic Contract

- **Component:** Loader
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Loader.Interaction.md) · [Styling](./Loader.Styling.md) · [Accessibility](./Loader.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Loader.tsx`
- **Catalog row:** #79 Loader (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** pure CSS animation; no third-party primitive (lucide `Loader2` for spinner glyph)

---

## 1. Purpose

Loader is the canonical **busy-state indicator** of `@harborline-software/ui-react`. It
communicates "this region is loading content" to the user during async
operations — initial page loads, data fetches, route transitions, form
submissions, etc.

This is a **forward-spec**: Loader has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the standard React spinner pattern, with provision for overlay
modes.

Loader is **presentational and stateless** — no internal state, no events.
It is purely a visual + ARIA indicator. The "busy" state is host-owned;
Loader simply renders the spinner.

The Harborline busy-state vocabulary has three siblings:

- **Loader** (this) — a spinner indicator, used standalone or as a region
  overlay. The atomic primitive.
- **Skeleton row treatment** (DataGrid M1) — DataGrid's built-in
  6-skeleton-row default while loading.
- **Button `loading` prop** (Button M2) — Button's own inline spinner
  during async actions.

Loader is the foundational primitive of this family. The skeleton-row
and inline-button-spinner treatments are component-specific
specialisations that may or may not use Loader internally.

---

## 2. Data model

Loader has no internal data model.

```typescript
type LoaderSize = 'xs' | 'sm' | 'md' | 'lg'
type LoaderVariant = 'spinner' | 'dots' | 'bar'

interface LoaderProps extends React.HTMLAttributes<HTMLDivElement> {
  size?: LoaderSize
  variant?: LoaderVariant
  label?: string
  inline?: boolean
}

interface LoaderOverlayProps {
  active: boolean
  size?: LoaderSize
  variant?: LoaderVariant
  label?: string
  blur?: boolean
  children: React.ReactNode
}
```

The component family has two pieces:

1. **`<Loader>`** — atomic spinner / dots / bar primitive.
2. **`<LoaderOverlay>`** — wraps a region; renders `children` underneath
   and overlays a Loader + scrim when `active`.

---

## 3. Props — semantics and defaults

### 3.1 `<Loader>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `size` | `'xs' \| 'sm' \| 'md' \| 'lg'` | `'md'` | Spinner dimensions. `'xs'` ≈ 12px, `'sm'` ≈ 16px, `'md'` ≈ 24px, `'lg'` ≈ 40px. PAO Styling owns exact tokens. |
| `variant` | `'spinner' \| 'dots' \| 'bar'` | `'spinner'` | Visual style. `'spinner'` is the canonical rotating ring/circle. `'dots'` is three pulsing dots. `'bar'` is an indeterminate progress bar (full-width). |
| `label` | `string` | `'Loading…'` | Accessible label announced to AT (`aria-label` or `aria-labelledby` target — PAO Accessibility decides). May be visually hidden or visible based on `inline`. |
| `inline` | `boolean` | `false` | When `true`, Loader renders as an inline-flow element with the `label` visible next to the spinner. When `false`, Loader renders as a block centered in its container with the `label` visually hidden (AT-only). |
| HTML attributes | — | — | Spread onto the root `<div>`. |

### 3.2 `<LoaderOverlay>`

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `active` | `boolean` | _required_ | When `true`, the overlay is rendered atop `children`. |
| `size` | `LoaderSize` | `'lg'` | Forwarded to the internal Loader; defaults larger because overlay context warrants more visual weight. |
| `variant` | `LoaderVariant` | `'spinner'` | Forwarded to the internal Loader. |
| `label` | `string` | `'Loading…'` | Forwarded to the internal Loader. |
| `blur` | `boolean` | `false` | When `true`, a backdrop-blur is applied behind the spinner. When `false`, a semi-transparent scrim is used. |
| `children` | `ReactNode` | _required_ | The region whose busy state the overlay represents. |

### 3.3 Variant semantics (closes G-LD3)

| Harborline `variant` | Visual | Closest Telerik `Type` |
| --- | --- | --- |
| `spinner` | Rotating circular ring. | `InfiniteSpinner` |
| `dots` | Three pulsing dots in a row. | `Pulsing` (Telerik default) — note: Harborline and Telerik default differ |
| `bar` | Indeterminate horizontal progress bar (sliding gradient). | No Telerik equivalent |

**Note for Telerik users:** Telerik Loader defaults to `Type='Pulsing'`
(three pulsing dots). Harborline defaults to `variant='spinner'` (rotating
ring). If you want the Telerik-equivalent default, pass `variant="dots"`.

PAO Styling owns the animation timing curves and the exact dimensions.

### 3.4 Inline vs block rendering (closes G-LD4)

**`inline === false` (default):**
- Loader is a centred block element. Width fills its container (typically
  the host wraps in a sized container — `<div class="h-32"><Loader /></div>`).
- Spinner is positioned absolutely or via flex-centre.
- `label` is **visually hidden**, announced to AT only.

**`inline === true`:**
- Loader is an inline-flex element. The spinner appears alongside the
  **visible `label` text**.
- Useful for "Refreshing…" inline status next to a button or in a row.

**Consequence:** If you want a centred loading spinner WITH a visible
caption below it (e.g. `"Saving…"` under a full-page overlay), you cannot
achieve this with `inline=false`. Two options:
1. Use `inline=true` — the label is visible but left-aligned with the
   spinner.
2. Compose manually: render `<Loader />` and a `<p>` in a flex-col wrapper.

A `caption: ReactNode` prop for block-mode visible captions is a planned
enhancement (Telerik LoaderContainer has `Text` for this; deferred §7).

### 3.5 LoaderOverlay layout

`<LoaderOverlay active={isLoading}><DataGrid … /></LoaderOverlay>` renders
the data table normally; when `active` is `true`:

- A semi-transparent scrim covers the children (default: `bg-white/60`).
- A `Loader size='lg'` is centred over the scrim.
- The children remain visually present (greyed) but are not interactive
  (pointer events captured by the scrim).
- When `blur === true`, a `backdrop-blur-sm` is applied to the scrim
  instead of (or in addition to) the semi-transparent fill.

#### 3.5.1 Host layout requirement (closes G-LD2)

`<LoaderOverlay>` internally renders a `position: relative` `<div>` wrapper
around its `children`. The scrim and spinner are positioned absolutely
within that wrapper. Two implications for hosts:

1. **Defined dimensions.** The overlay fills the wrapper's bounds — the
   wrapper takes its height from `children`. When `children` renders a
   sized component (DataGrid, full-height panel), the bounds are set
   naturally. When `children` are sparse or absent (pure loading state
   with no content yet), the host must supply a minimum height:
   ```tsx
   <LoaderOverlay active className="min-h-[200px]">…</LoaderOverlay>
   ```
2. **Stacking context.** The `position: relative` creates a stacking
   context. Do not wrap `<LoaderOverlay>` in a parent with a lower
   `z-index` or the scrim may be visually clipped beneath a sibling.
   PAO Styling owns the overlay `z-index` token.

### 3.6 HTML attribute passthrough

Loader spreads HTML attributes onto its root `<div>`. LoaderOverlay does
not (the root is a wrapping `<div>` whose structure is implementation
detail; hosts who need attributes apply them to the parent).

---

## 4. Events — semantics

Loader and LoaderOverlay have **no events**. Both are presentational.

---

## 5. Slots

Loader has no slot extensibility in M2 — the visible content is the
spinner glyph + optional inline label.

LoaderOverlay's only slot is `children` (the wrapped region).

---

## 6. Component composition

- **Page-level loading.** When a route initial-loads, the route component
  renders `<Loader size='lg' />` in its main region while data fetches.
- **Card-content loading.** A card whose data is loading renders Loader
  as the CardContent body, or wraps the entire CardContent in
  LoaderOverlay.
- **DataGrid busy state.** DataGrid has its own skeleton-row treatment
  (DataGrid.Semantic.md §6). Hosts who want a different treatment can
  wrap DataGrid in LoaderOverlay instead.
- **Inline status indicator.** "Syncing your changes…" inline with a
  small spinner: `<Loader size='xs' inline label='Syncing your changes' />`.
- **Form submission.** Button's `loading` prop is the canonical pattern.
  Loader as a standalone next to a form is rare.
- **Region refresh.** A panel refreshing in the background uses
  LoaderOverlay so the existing content stays visible (greyed) while
  refresh runs — better than swapping to a full-screen spinner.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **Determinate progress** — a progress bar showing percent complete.
  This is a separate ProgressBar component (catalog #94, future wave).
- **Custom spinner glyph** — hosts cannot swap the icon in M2.
- **Custom scrim colour / opacity for LoaderOverlay** — controlled by
  PAO Styling tokens; per-instance override deferred.
- **Delayed render** — Loader appearing only after a short delay (e.g.
  150ms) to avoid flashing for fast operations. Useful but adds
  complexity; can be applied per-host with their own timing.
- **Pulse / shimmer variants** — for skeleton-style content rather than
  a spinner. The skeleton-row treatment in DataGrid handles the
  table-cell case; a generic skeleton block is a future component
  (Skeleton, catalog #122).
- **Loader as a slot inside other components** — e.g. Button's `loading`
  prop currently uses its own internal spinner. Council open question 1
  considers consolidating.
- **Custom indicator placement** — top / bottom / left / right inside
  LoaderOverlay. Centred-only for M2.

---

## 8. Open questions (for council)

1. **Loader reuse inside Button.** Button.Semantic.md §3.3 specifies
   Button's `loading` prop renders an internal spinner. Should Button
   import Loader rather than have its own spinner implementation?
   Leaning yes — single source of truth — but the Button spec doesn't
   require it.
2. **Default variant.** `'spinner'` (this spec) or `'dots'`? `'spinner'`
   is the universal idiom; `'dots'` reads as lighter-weight but less
   common. Leaning `'spinner'`.
3. **Default size.** `'md'` (24px) — Loader as a standalone block is
   typically larger; LoaderOverlay default is `'lg'`. Should standalone
   default to `'md'` (this spec) or `'lg'`? Leaning `'md'` — overlay
   has its own size default; standalone use cases vary.
4. **LoaderOverlay `active` semantics during transition.** When `active`
   flips from `false` → `true`, does the overlay fade in or appear
   instantly? Today: depends on PAO Styling — animation choice. Confirm
   PAO is the right owner.
5. **`bar` variant — position inside container.** Top-pinned (default)
   or bottom? Some libraries pin top of card content (canonical), others
   span across the centre. Leaning top — matches typical refresh pattern.
6. **Should there be a `<LoaderProvider>`** for app-wide loading state
   (e.g. driven by router navigation events)? Today: hosts wire
   their own. Leaning defer — adding cross-cutting "global loading"
   without a clear use case has scope-creep risk.
7. **Accessibility — `role="status"` vs `role="progressbar"`.** PAO
   Accessibility decides; flagging because the choice impacts how the
   Loader announces. `role="status"` + `aria-live="polite"` is the
   indeterminate-spinner default.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/loaders/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LD1 | Critical | LoaderOverlay scope (fullscreen vs scoped) undocumented | [RESOLVED 2026-06-05] §3.5.1: scrim is absolutely positioned relative to LoaderOverlay wrapper; host must set position:relative on parent |
| G-LD2 | High | position:relative requirement undocumented | [RESOLVED 2026-06-05] §3.5.1 explicit: "host container MUST set `position: relative`" |
| G-LD3 | High | Type parity table absent | [RESOLVED 2026-06-05] §3.3 comparison: spinner=InfiniteSpinner, dots=Pulsing, bar=no Telerik equivalent |
| G-LD4 | High | Inline label visibility behavior surprises | [RESOLVED 2026-06-05] §3.4: inline=spinner+label left-aligned; non-inline=spinner only+AT-only label |
| G-LD5 | Medium | OverlayThemeColor (light/dark scrim) absent | [ACCEPTED-RISK 2026-06-05] PAO Styling owns scrim color token; bg-white/60 in M1 |
| G-LD6 | Medium | LoaderPosition variant | [ACCEPTED-RISK 2026-06-05] inline mode is always spinner-then-label; deferred |
| G-LD7 | Medium | Delayed-render recipe absent | [ACCEPTED-RISK 2026-06-05] §6 recipe added: useEffect + 150ms timer before rendering Loader |

---

## 16. Wave-N expansion — themeColor (FR-3) + converging-spinner type variant (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (Loader 55% coverage)
**Rulings applied:** FR-3 (themeColor axis)

### 16.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| `themeColor` entirely absent from `<Loader>` props | P1 | `themeColor` added — §16.2 |
| `themeColor` only 2 implied values vs Kendo's 4 | P2 | Full FR-3 set documented — §16.3 |
| `type` third variant `"converging-spinner"` missing | P2 | `variant="converging-spinner"` added — §16.4 |
| `ariaLabel` prop naming (camelCase vs `aria-label` attr) | P2 | Reconciliation note — §16.5 |

### 16.2 Updated data model additions

```typescript
// FR-3 themeColor axis for Loader
// Kendo Loader supports base/primary/secondary/tertiary; Harborline extends to
// the full FR-3 8-value set. 'base' maps to omitting the prop (neutral track colour).
type LoaderThemeColor =
  | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error'
  | 'inverse'

// Extended variant list
type LoaderVariant = 'spinner' | 'dots' | 'bar' | 'converging-spinner'

// Extended LoaderProps (additions only — all existing props unchanged)
interface LoaderPropsAdditions {
  /** FR-3 themeColor axis. Controls the colour of the spinner track and
   *  animated fill. When omitted, the loader uses the default track colour
   *  (`--sf-loader-track-default`, typically a neutral gray).
   *  Kendo: `themeColor` (base/primary/secondary/tertiary); Harborline extends
   *  to the full FR-3 8-value palette. */
  themeColor?: LoaderThemeColor
}
```

Updated full `LoaderProps`:

```typescript
interface LoaderProps extends React.HTMLAttributes<HTMLDivElement> {
  size?:       'xs' | 'sm' | 'md' | 'lg'               // default: 'md'
  variant?:    'spinner' | 'dots' | 'bar' | 'converging-spinner'  // default: 'spinner'
  label?:      string                                    // default: 'Loading…'
  inline?:     boolean                                   // default: false
  themeColor?: LoaderThemeColor                          // NEW — default: (base)
}
```

### 16.3 themeColor semantics

| `themeColor` value | Track / fill colour | Typical use |
|---|---|---|
| (omitted) | `--sf-loader-track-default` (neutral gray) | Default; content-loading spinner |
| `primary` | Brand primary colour | Deliberate brand-aligned loading |
| `secondary` | Brand secondary | Secondary actions (e.g., background sync) |
| `tertiary` | Brand tertiary | Tertiary / minor operations |
| `info` | Info blue | Information-fetch loading |
| `success` | Success green | Post-success confirmation overlay |
| `warning` | Warning amber | Degraded-mode loading |
| `error` | Error red | Error-recovery in progress (rare) |
| `inverse` | Inverted (white on dark scrim) | LoaderOverlay dark-mode or coloured-scrim contexts |

PAO Styling maps each `themeColor` value to a `--sf-loader-*` token.

`themeColor` is orthogonal to `variant` — all four variants support all themeColor values.

### 16.4 converging-spinner variant

`variant="converging-spinner"` renders two arcs that rotate toward and away from each other — a "breathing" pattern distinct from the single-ring spinner. Kendo: `Type='ConvergingSpinner'`.

| Variant | Motion | Closest Kendo `Type` |
|---|---|---|
| `spinner` | Single ring rotating | `InfiniteSpinner` |
| `dots` | Three pulsing dots | `Pulsing` |
| `bar` | Indeterminate horizontal bar | (no Kendo equivalent) |
| `converging-spinner` | Dual arcs converging/diverging | `ConvergingSpinner` |

Animation timing is PAO Styling territory (`--sf-loader-duration`, `--sf-loader-easing`).

### 16.5 `ariaLabel` prop naming reconciliation

Kendo Loader uses `ariaLabel` (camelCase prop) as a convenience shorthand; it maps to the HTML `aria-label` attribute on the rendered element. Harborline exposes `label` (the string content) and emits `aria-label` or `aria-labelledby` from it (PAO Accessibility contract owns the exact emission). The naming difference is a React prop vs HTML attribute distinction — functionally equivalent. Harborline hosts use `label="Loading orders…"` not `ariaLabel`; Kendo-to-Harborline migration replaces `ariaLabel` with `label`.

### 16.6 LoaderOverlay themeColor

`LoaderOverlay` forwards `themeColor` to its internal `Loader`:

```typescript
interface LoaderOverlayProps {
  active:       boolean
  size?:        LoaderSize           // default: 'lg'
  variant?:     LoaderVariant        // default: 'spinner'
  label?:       string               // default: 'Loading…'
  blur?:        boolean              // default: false
  themeColor?:  LoaderThemeColor     // NEW — forwarded to internal Loader
  children:     React.ReactNode
}
```

`themeColor="inverse"` is the recommended choice when `blur=true` on a dark overlay (ensures the spinner is visible on the dark scrim).

### 16.7 Deferred from this wave

- **Custom animation keyframes** — PAO Styling owns animation; per-instance overrides deferred.
- **`type="bar"` with custom colour segments** — fringe use case; deferred.
- **Loader-as-slot inside Button** — OQ from §8.1; still deferred to Button wave.
