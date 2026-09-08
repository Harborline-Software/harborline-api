# Card — Styling Contract

- **Component:** Card
- **ADR 0017 family:** Layout
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Card.Semantic.md) · [Interaction](./Card.Interaction.md) · [Accessibility](./Card.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Card_
- **Catalog row:** #21 Card (`app-priority: high`, `library-scope: v1`, `Notes: shadcn Card`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Card is a structural-composition primitive: a bounded region with optional
header, body, and footer slots, used to group related content (a property
summary, a list item with actions, a dashboard tile). This contract names
the `--sf-card-*` token surface (chrome — background, border, radius,
shadow, padding) and the per-slot spacing rhythm — and pins the Tailwind
class recipes that act as the M2 default styling layer.

Card is **structural**. It does not carry interactivity (no hover, no
focus, no click — those belong to interactive children if present). The
exception is when an entire Card surface is wrapped in a link/button (a
"clickable card"); the interaction tokens for that pattern live in the
Interaction contract, not the Styling token surface — Card itself stays
chrome-only.

---

## 2. Token surface

Card exposes the following CSS custom properties (the `--sf-card-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Chrome tokens (variant-axis: `flat` | `raised` | `outlined` | `elevated`)

| Token | Semantic role | Varies by variant | Notes |
|---|---|---|---|
| `--sf-card-bg-{variant}` | Background fill | yes | Typically `--sf-color-surface` (white) for all variants; `subtle` MAY tint slightly |
| `--sf-card-fg` | Default foreground (body text colour) | no (shared) | Pairs with `--sf-card-bg` at ≥ 4.5:1 |
| `--sf-card-border-{variant}` | Border colour | yes | `default` and `outlined` carry a border; `elevated` and `subtle` resolve to `transparent` |
| `--sf-card-border-width-{variant}` | Border width | yes | `default` / `outlined`: `1px`; `elevated` / `subtle`: `0` |
| `--sf-card-radius` | Corner radius | no (shared) | Typically `var(--sf-radius-lg)` — slightly larger than buttons for visual containment |
| `--sf-card-shadow-{variant}` | Box shadow | yes | `default`: subtle 1-px shadow; `elevated`: heavier shadow; `outlined`: none; `subtle`: none |

The four variants:

| `variant` | Chrome signature | Use case |
|---|---|---|
| `flat` | No border + no shadow + tinted bg | Background-grouping; minimal visual weight; logical section grouping |
| `raised` | Light border + subtle shadow | Standard content card; most common variant |
| `outlined` | Solid border + no shadow | Print-friendly / flat design; emphasises boundary |
| `elevated` | No border + medium shadow | Floating / above-list pattern; emphasises separation |

### 2.2 Padding tokens (padding axis: `none` | `sm` | `md` | `lg`)

Slot-level padding rhythm. Header / body / footer share the horizontal
padding token but each may carry its own vertical padding.

| Token | Semantic role | Varies by padding | Notes |
|---|---|---|---|
| `--sf-card-padding-x-{padding}` | Shared horizontal padding for all slots | yes | `none`: `0` / `sm`: `1rem` / `md`: `1.5rem` / `lg`: `2rem` |
| `--sf-card-header-padding-y-{padding}` | Header vertical padding | yes | `none`: `0` / `sm`: `0.75rem` / `md`: `1rem` / `lg`: `1.25rem` |
| `--sf-card-body-padding-y-{padding}` | Body vertical padding | yes | Same as header by default; may differ if design system separates |
| `--sf-card-footer-padding-y-{padding}` | Footer vertical padding | yes | Same as header by default |
| `--sf-card-slot-gap-{padding}` | Vertical gap between adjacent slots when no separator | yes | `0` by default (slots are flush); separator (§2.3) opts in to a divider line |

### 2.3 Separator token

| Token | Semantic role | Varies | Notes |
|---|---|---|---|
| `--sf-card-separator-color` | Optional divider between header/body and body/footer | none | Pairs with `--sf-card-bg-{variant}` at ≥ 3:1 per WCAG 2.2 SC 1.4.11 when shown. The separator is opt-in via `separators?: boolean` prop |

### 2.4 Typography tokens (header)

The header slot has built-in typography rhythm because it usually holds a
title + optional description.

| Token | Semantic role | Varies | Notes |
|---|---|---|---|
| `--sf-card-title-font-size` | Title font size | no | Typically `1.125rem` (`text-lg`) |
| `--sf-card-title-font-weight` | Title font weight | no | Typically `600` (`font-semibold`) |
| `--sf-card-title-fg` | Title foreground | no | Resolves to `--sf-card-fg` by default; may diverge for a tinted accent (rare) |
| `--sf-card-description-font-size` | Description font size | no | Typically `0.875rem` (`text-sm`) |
| `--sf-card-description-fg` | Description foreground | no | Pairs with `--sf-card-bg-{variant}` at ≥ 4.5:1; intentionally lower-contrast than title |

Body and footer slots inherit body typography from the surrounding theme;
Card does NOT pin body-text tokens (those belong to child content).

### 2.5 Token additions to `layout.tokens.json` (NEW)

The `--sf-card-*` family lives in a new `layout.tokens.json` file under
`_shared/design/tokens/`. The file holds Card plus future Layout-family
primitives (Stack, Grid-layout, Splitter, etc.).

Recommended initial structure:

```jsonc
{
  "$schema": "https://design-tokens.github.io/community-group/format/",
  "_meta": {
    "name": "layout.tokens",
    "description": "Default design-system values for the Layout component family. Components consume these via CSS custom properties (--sf-card-*, etc.).",
    "adr": "0017-A1",
    "status": "Draft",
    "version": "0.1.0"
  },
  "card": {
    "_intent": "four variants × four padding sizes; structural chrome only — no interactive states",
    "variant": {
      "flat":     { "bg": "#F9FAFB", "border": "transparent", "borderWidth": "0", "shadow": "none" },
      "raised":   { "bg": "#FFFFFF", "border": "#E5E7EB", "borderWidth": "1px", "shadow": "0 1px 2px rgba(17, 24, 39, 0.04)" },
      "outlined": { "bg": "#FFFFFF", "border": "#D1D5DB", "borderWidth": "1px", "shadow": "none" },
      "elevated": { "bg": "#FFFFFF", "border": "transparent", "borderWidth": "0", "shadow": "0 4px 6px -1px rgba(17, 24, 39, 0.10), 0 2px 4px -2px rgba(17, 24, 39, 0.06)" }
    },
    "padding": {
      "none": { "paddingX": "0",      "headerPaddingY": "0",       "bodyPaddingY": "0",       "footerPaddingY": "0" },
      "sm":   { "paddingX": "1rem",   "headerPaddingY": "0.75rem", "bodyPaddingY": "0.75rem", "footerPaddingY": "0.75rem" },
      "md":   { "paddingX": "1.5rem", "headerPaddingY": "1rem",    "bodyPaddingY": "1rem",    "footerPaddingY": "1rem" },
      "lg":   { "paddingX": "2rem",   "headerPaddingY": "1.25rem", "bodyPaddingY": "1.25rem", "footerPaddingY": "1.25rem" }
    },
    "radius": "8px",
    "fg": "#111827",
    "separatorColor": "#E5E7EB",
    "title": {
      "fontSize": "1.125rem",
      "fontWeight": 600
    },
    "description": {
      "fontSize": "0.875rem",
      "fg": "#6B7280"
    },
    "_notes": {
      "contrast": "Default body text on every variant background is verified ≥ 4.5:1 (WCAG 2.2 SC 1.4.3). Description text on every variant background is verified ≥ 4.5:1. Border on background ≥ 3:1 where border carries shape (WCAG 2.2 SC 1.4.11).",
      "clickableCard": "When the entire card surface is wrapped in <a> or <button>, that parent owns hover/focus/active styling. Card tokens do NOT include interaction states. Recommended hover treatment for clickable cards: subtle shadow lift OR a 4px brand-tinted border (consumer choice).",
      "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets and toggle via [data-theme='dark'] or equivalent.",
      "density": "These defaults are 'default' density. 'compact' / 'comfortable' variants are out of scope for M2; revisit if dashboards demand per-density tokens."
    }
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Base recipe

```
flex flex-col bg-white text-gray-900 rounded-lg
```

### 3.2 Variant recipes

| Variant | Tailwind recipe |
|---|---|
| `flat` | `bg-gray-50` |
| `raised` | `bg-white border border-gray-200 shadow-sm` |
| `outlined` | `bg-white border border-gray-300` |
| `elevated` | `bg-white shadow-md` |

### 3.3 Padding recipes

| Padding | Tailwind recipe (slot padding) |
|---|---|
| `none` | `p-0` (all slots flush) |
| `sm` | header/body/footer: `px-4 py-3` |
| `md` | header/body/footer: `px-6 py-4` |
| `lg` | header/body/footer: `px-8 py-5` |

### 3.4 Slot composition

The shadcn Card pattern composes named subcomponents:

```jsx
<Card variant="raised" padding="md">
  <CardHeader>
    <CardTitle>Title</CardTitle>
    <CardDescription>Optional description</CardDescription>
  </CardHeader>
  <CardContent>
    /* arbitrary content */
  </CardContent>
  <CardFooter>
    /* action buttons or summary */
  </CardFooter>
</Card>
```

Each subcomponent maps to a slot in the token system:

| Subcomponent | Padding tokens | Tailwind recipe |
|---|---|---|
| `CardHeader` | `--sf-card-padding-x-{padding}` + `--sf-card-header-padding-y-{padding}` | `flex flex-col gap-1.5 px-6 py-4` |
| `CardTitle` | `--sf-card-title-*` | `text-lg font-semibold leading-none` |
| `CardDescription` | `--sf-card-description-*` | `text-sm text-gray-500` |
| `CardContent` | `--sf-card-padding-x-{padding}` + `--sf-card-body-padding-y-{padding}` | `px-6 py-4` |
| `CardFooter` | `--sf-card-padding-x-{padding}` + `--sf-card-footer-padding-y-{padding}` | `flex items-center px-6 py-4` |

### 3.5 Separator recipe (opt-in via `separators` prop)

The divide direction is orientation-dependent:

```
// vertical orientation (default):
divide-y divide-gray-200

// horizontal orientation:
divide-x divide-gray-200
```

`divide-y` paints horizontal lines (correct for `flex-col`). `divide-x` paints vertical lines (correct for `flex-row`). Using `divide-y` in horizontal mode produces lines that run along the wrong axis. When `separators={true}`, apply the correct divide class based on `orientation`. The line colour is `--sf-card-separator-color`.

### 3.6 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer. A consumer who
wants to re-theme Card consumes the **tokens**, not the Tailwind classes.

---

## 4. Visual state inventory

Card is non-interactive. The state matrix is trivial.

| State | Trigger | Notes |
|---|---|---|
| **default** | always | Single visual state per variant |
| **hover/focus/active** | — | NOT supported at Card level. A clickable-card pattern (Card wrapped in `<a>` or `<button>`) gives those states to the parent, NOT Card |
| **disabled** | — | NOT applicable; Card has no enabled/disabled axis |
| **loading** | — | NOT applicable. If a card needs a loading state, it composes a Loader as a child |

**Clickable-card hover treatment.** Documented here as a reference recipe
because it's a frequent pattern, but the styling is owned by the parent:

```jsx
<a href="..." class="block transition-shadow hover:shadow-md focus-visible:ring-2 focus-visible:ring-blue-600 focus-visible:ring-offset-2 rounded-lg">
  <Card variant="outlined">...</Card>
</a>
```

The Card stays variant-neutral; the parent `<a>` adds the lift on hover and
the focus ring.

---

## 5. Responsive behaviour

Card does not reflow or change size based on viewport. The four padding values
(`none` / `sm` / `md` / `lg`) are author-chosen per usage context.

Card does **not** at M2:

- Stack horizontal cards vertically on narrow viewports — that's a parent
  layout concern (Grid, Stack).
- Collapse the header into the body on narrow viewports.
- Swap variant by viewport.

---

## 6. Reduced motion

Card has NO animations by default. No reduced-motion handling required.

If a clickable-card pattern uses `transition-shadow` on hover, that
transition MUST honor `prefers-reduced-motion: reduce` (using
`motion-reduce:transition-none`). That responsibility lies with the
parent (the interactive wrapper), not Card.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **CardActions vs. CardFooter.** shadcn ships `CardFooter`; Vaadin
   ships `<vaadin-card-actions slot="footer">`. The contract uses
   `CardFooter` (matches shadcn naming) but the slot serves both general
   footer content AND action buttons. Decision: one `CardFooter` slot;
   council confirms whether to introduce a second `CardActions` slot for
   semantic specificity.
2. **Image media slot.** Some Cards have a hero image above the header.
   Should there be a dedicated `CardMedia` slot with zero internal
   padding so the image bleeds edge-to-edge? Recommended: yes, but
   defer to M3 (the M2 scope ships header/body/footer only).
3. **Per-variant focus-ring colour for clickable cards.** Out of Card's
   scope; the wrapping `<a>` or `<button>` carries its own focus-ring
   recipe.
4. **Density variants (compact / default / comfortable).** Out of M2
   scope. Dashboards may demand them; revisit per-product-surface in M3.
5. **Visual hierarchy across variants.** `elevated` is heavier than
   `default`. Some systems use elevation as a z-axis surrogate (modals
   are most-elevated). This contract treats elevation as a flat axis
   (one variant among four), NOT as a z-index proxy.

---

## 8. Do / Don't

### Do

- Use named subcomponents (`CardHeader` / `CardContent` / `CardFooter`) so
  slot-level padding tokens apply consistently.
- Pair body text foreground with the variant's background at ≥ 4.5:1.
- Use `outlined` variant in print stylesheets (shadow doesn't print).
- For clickable cards, wrap the Card in `<a>` or `<button>` and let the
  wrapper own hover/focus/active.

### Don't

- Don't ship a Card with hover/focus/active visual states on the Card
  surface itself. Those belong to the wrapping interactive parent (if
  any).
- Don't use Card as the only signal for "clickable" — the wrapping `<a>`
  or `<button>` carries that signal via cursor + focus ring.
- Don't put hex values in component code. Component CSS consumes
  `var(--sf-card-*)` only.
- Don't nest Cards more than one level deep without strong rationale —
  the chrome compounds visually.
- Don't use `flat` variant for content that needs an emphasised
  boundary; reach for `raised` or `outlined`.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Card —
  `<div>` with variant + size CVA classes; named subcomponents.
- **Web Components (Phase M4, Lit):** TBD. Vaadin's `<vaadin-card>`
  pattern uses named slots; the WC track may inherit that shape.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #21 Card (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Card — reference foundation
- [Card.Semantic.md](./Card.Semantic.md) — prop contract
- [Card.Interaction.md](./Card.Interaction.md) — behavioural contract
- [Card.Accessibility.md](./Card.Accessibility.md) — heading hierarchy, landmark roles
- `_shared/design/tokens/layout.tokens.json` (proposed; NEW file) — default token values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
