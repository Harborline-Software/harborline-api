# Drawer — Styling Contract

- **Component:** Drawer (Sheet)
- **ADR 0017 family:** Overlays
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Drawer.Semantic.md) · [Interaction](./Drawer.Interaction.md) · [Accessibility](./Drawer.Accessibility.md)
- **Related contract:** [Dialog.Styling.md](./Dialog.Styling.md) — Drawer shares the focus-trap + Escape + return-focus contract with Dialog; this contract specialises the edge-anchored sheet pattern.
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Sheet (built on Radix Dialog)_
- **Catalog row:** #46 Drawer (`app-priority: high`, `library-scope: v1`, `Notes: alias: Sheet (shadcn)`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Drawer is an edge-anchored modal panel that slides into the viewport from
one of the four sides (right / left / top / bottom). It is distinct from
Dialog (centred modal) and from SideNav's drawer mode (which is a
navigation surface, not a generic overlay). Drawer hosts task-flow content:
a multi-field form, a detail-view side panel, a filter panel, a settings
sheet on mobile.

This contract names the `--sf-drawer-*` token surface and the slide-in /
slide-out animation pattern. Drawer composes on Radix Dialog primitives
(via the shadcn Sheet pattern) — same focus trap, same `aria-modal`, same
return-focus semantics as Dialog, but with edge anchoring instead of
centring.

---

## 2. Token surface

Drawer exposes the following CSS custom properties (the `--sf-drawer-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Backdrop

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-drawer-overlay-bg` | Backdrop colour (with alpha) | open | Typically `rgba(0, 0, 0, 0.5)` — slightly darker than Dialog's 0.4 because Drawer panels can be opaque + at-edge so the page chrome behind is more visible |
| `--sf-drawer-overlay-z-index` | Backdrop stacking order | none | Numeric; M2 baseline `50` (matches Dialog) |

### 2.2 Panel — side axis (right / left / top / bottom)

| Token | Semantic role | Varies by | Notes |
|---|---|---|---|
| `--sf-drawer-content-bg` | Panel background fill | none | Typically `--sf-color-surface` (white) |
| `--sf-drawer-content-shadow-{side}` | Panel drop-shadow facing INTO the viewport | side | Right-side drawer: shadow extends LEFT; left-side: shadow extends RIGHT; etc. |
| `--sf-drawer-z-index` | Panel stacking order | none | Numeric; M2 baseline `50` |

### 2.3 Panel — size axis (sm / md / lg / xl)

Drawer panels have a "size" along the axis perpendicular to their anchor
edge. For right / left drawers, size is WIDTH; for top / bottom drawers,
size is HEIGHT.

| Token | Semantic role | Varies by size | Notes |
|---|---|---|---|
| `--sf-drawer-panel-size-{size}` | Panel WIDTH (right/left) or HEIGHT (top/bottom) | sm / md / lg / xl / full | sm: `20rem` (320px) / md: `24rem` (384px) / lg: `32rem` (512px) / xl: `40rem` (640px) / full: `100%` |

The opposite-axis dimension is always `100vh` (right/left) or `100vw`
(top/bottom) — Drawer fills the entire edge.

### 2.4 Header / body / footer slots

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-drawer-header-padding` | Header internal padding | Typically `1.5rem` (`p-6`) |
| `--sf-drawer-header-border-bottom` | Bottom divider between header and body | Pairs with content-bg at ≥ 3:1 |
| `--sf-drawer-title-fg` | Title text colour | Pairs at ≥ 4.5:1 |
| `--sf-drawer-title-font-size` | Title font size | Typically `1.125rem` (`text-lg`) |
| `--sf-drawer-title-font-weight` | Title font weight | Typically `600` (`font-semibold`) |
| `--sf-drawer-description-fg` | Description text colour | Pairs at ≥ 4.5:1; lower-contrast than title |
| `--sf-drawer-description-font-size` | Description font size | Typically `0.875rem` (`text-sm`) |
| `--sf-drawer-body-padding` | Body internal padding | Typically `1.5rem` (`p-6`) |
| `--sf-drawer-body-overflow` | Body overflow handling | `auto` — body scrolls internally when content exceeds drawer height |
| `--sf-drawer-footer-padding` | Footer internal padding | Typically `1.5rem` (`p-6`) |
| `--sf-drawer-footer-border-top` | Top divider above footer | Pairs at ≥ 3:1 |
| `--sf-drawer-footer-bg` | Footer background (subtle separation) | OPTIONAL; `transparent` by default, MAY resolve to `--sf-color-neutral-50` for a tinted footer |

### 2.5 Close button

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-drawer-close-fg` | Close-icon colour | default | Pairs at ≥ 3:1 against header bg |
| `--sf-drawer-close-fg-hover` | Hover state | hover | Darker shade |
| `--sf-drawer-close-bg-hover` | Background on hover | hover | Tinted neutral overlay |
| `--sf-drawer-close-ring-focus` | Focus-visible ring | focus | Brand-accent; pairs at ≥ 3:1 |
| `--sf-drawer-close-radius` | Corner radius | none | Typically `var(--sf-radius-sm)` |
| `--sf-drawer-close-size` | Hit-area dimensions | none | MUST be ≥ `2rem` (32px) per WCAG 2.5.8 + spacing exception |

### 2.6 Animation tokens

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-drawer-enter-duration` | Slide-in duration | Typically `300ms`; MUST be suppressed under reduced-motion |
| `--sf-drawer-exit-duration` | Slide-out duration | Typically `200ms` |
| `--sf-drawer-overlay-fade-duration` | Backdrop fade duration | Typically matches drawer transition for synchronised motion |

### 2.7 Token additions to `overlays.tokens.json` (NEW per Drawer; complements the M1-proposed file for Dialog)

The `--sf-drawer-*` family extends the `overlays.tokens.json` file (created
in M1 Batch C contracts; if not yet authored to file, this contract is
the source of truth for the eventual JSON values).

Recommended namespace addition:

```jsonc
"drawer": {
  "_intent": "edge-anchored modal sheet; four sides × five sizes; same focus-trap and return-focus semantics as Dialog",
  "overlayBg": "rgba(0, 0, 0, 0.5)",
  "zIndex": 50,
  "content": {
    "bg": "#FFFFFF",
    "shadow": {
      "right": "-4px 0 16px rgba(17, 24, 39, 0.10)",
      "left":  "4px 0 16px rgba(17, 24, 39, 0.10)",
      "top":   "0 4px 16px rgba(17, 24, 39, 0.10)",
      "bottom": "0 -4px 16px rgba(17, 24, 39, 0.10)"
    }
  },
  "panelSize": {
    "sm":   "20rem",
    "md":   "24rem",
    "lg":   "32rem",
    "xl":   "40rem",
    "full": "100%"
  },
  "header": {
    "padding": "1.5rem",
    "borderBottom": "#E5E7EB",
    "titleFg": "#111827",
    "titleFontSize": "1.125rem",
    "titleFontWeight": 600,
    "descriptionFg": "#6B7280",
    "descriptionFontSize": "0.875rem"
  },
  "body": {
    "padding": "1.5rem",
    "overflow": "auto"
  },
  "footer": {
    "padding": "1.5rem",
    "borderTop": "#E5E7EB",
    "bg": "transparent"
  },
  "close": {
    "fg": "#6B7280",
    "fgHover": "#111827",
    "bgHover": "#F3F4F6",
    "ringFocus": "#2563EB",
    "radius": "0.25rem",
    "size": "2rem"
  },
  "animation": {
    "enterDuration": "300ms",
    "exitDuration": "200ms",
    "overlayFadeDuration": "200ms"
  },
  "_notes": {
    "contrast": "Title fg / description fg on content-bg verified ≥ 4.5:1 (WCAG 2.2 SC 1.4.3). Header / footer divider on content-bg verified ≥ 3:1 (WCAG 2.2 SC 1.4.11).",
    "sideShadowDirection": "Each side's shadow extends INTO the viewport (away from the screen edge). Right drawer shadow extends LEFT; left drawer shadow extends RIGHT; etc.",
    "reducedMotion": "All animations MUST honor prefers-reduced-motion: reduce — replace slide+fade with instant appearance/disappearance. The accessibility contract (focus trap, return-focus) is unchanged.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

Composes on Radix Dialog + a side-aware data-attribute for animation
direction (shadcn Sheet pattern).

### 3.1 Backdrop

```jsx
<DialogPrimitive.Overlay class="fixed inset-0 z-50 bg-black/80
  data-[state=open]:animate-in data-[state=open]:fade-in-0
  data-[state=closed]:animate-out data-[state=closed]:fade-out-0
  motion-reduce:animate-none" />
```

### 3.2 Panel per side

**⚠ Tailwind v3 constraint — no chained data-attribute variants.** Tailwind v3 does NOT support chaining two `data-[…]:` variants (e.g., `data-[side=right]:data-[state=open]:slide-in-from-right`). The variant compiler collapses them at build time — one of the two attribute conditions is silently dropped. Use Tailwind v4 (which supports nested variants) OR use the JS-split pattern below (v3-safe):

```jsx
// Compute side-specific animation classes in JS — one set per side:
const sideClasses = {
  right:  { position: 'inset-y-0 right-0 h-full w-96', animateIn: 'slide-in-from-right', animateOut: 'slide-out-to-right' },
  left:   { position: 'inset-y-0 left-0  h-full w-96', animateIn: 'slide-in-from-left',  animateOut: 'slide-out-to-left'  },
  top:    { position: 'inset-x-0 top-0   w-full h-96', animateIn: 'slide-in-from-top',   animateOut: 'slide-out-to-top'   },
  bottom: { position: 'inset-x-0 bottom-0 w-full h-96', animateIn: 'slide-in-from-bottom', animateOut: 'slide-out-to-bottom' },
}
const { position, animateIn, animateOut } = sideClasses[side]

<DialogPrimitive.Content
  class={cn(
    'fixed z-50 flex flex-col bg-white shadow-xl',
    position,
    'data-[state=open]:animate-in data-[state=open]:' + animateIn,
    'data-[state=closed]:animate-out data-[state=closed]:' + animateOut,
    'duration-300 motion-reduce:animate-none',
  )}>
  {/* header / body / footer */}
</DialogPrimitive.Content>
```

### 3.3 Header / body / footer recipes

| Region | Tailwind recipe |
|---|---|
| Header | `flex items-start justify-between gap-3 p-6 border-b border-gray-200` |
| Title | `text-lg font-semibold leading-none text-gray-900` |
| Description | `mt-1 text-sm text-gray-500` |
| Close button | `inline-flex h-8 w-8 items-center justify-center rounded text-gray-500 hover:bg-gray-100 hover:text-gray-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 focus-visible:ring-offset-2` |
| Body | `flex-1 overflow-y-auto p-6` |
| Footer | `flex items-center justify-end gap-3 border-t border-gray-200 p-6` |

### 3.4 Size recipes

Width (right / left) or height (top / bottom):

| Size | Width / Height class |
|---|---|
| `sm` | `w-80` (right/left) or `h-80` (top/bottom) |
| `md` (default) | `w-96` (right/left) or `h-96` (top/bottom) |
| `lg` | `w-[32rem]` or `h-[32rem]` |
| `xl` | `w-[40rem]` or `h-[40rem]` |
| `full` | `w-full` (mobile usually) or `h-full` |

### 3.5 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer.

---

## 4. Visual state inventory

Same state structure as Dialog plus side-aware enter/exit motion.

| State | Trigger | Visual |
|---|---|---|
| **closed** | default | Drawer + backdrop both unmounted (Radix portal mode) |
| **opening** | `open` becomes true | Backdrop fades in; panel slides in from anchored side per `--sf-drawer-enter-duration` |
| **open** | drawer is mounted and visible | Default visual state; focus trapped inside |
| **closing** | `open` becomes false (Escape, scrim click, close button, programmatic) | Backdrop fades out; panel slides out per `--sf-drawer-exit-duration` |

Close-button state matrix (default / hover / focus-visible) per §2.5.

---

## 5. Responsive behaviour

| Viewport | Default behaviour |
|---|---|
| Mobile (< 640px) | Drawer SHOULD use `size="full"` OR `size="lg"` depending on use case; right-anchored drawers MAY be uncomfortable on mobile (tap-target reach), prefer bottom-anchored for mobile-first surfaces |
| Tablet / Desktop (≥ 640px) | Default size per consumer; right-anchored is typical for detail panels; left for nav-like content (though SideNav's drawer covers that case) |

The contract does NOT auto-pivot anchor by viewport (that's a separate
"Sheet swaps to bottom on mobile" composition; engineer chooses).

---

## 6. Reduced motion

All slide-in / slide-out + backdrop-fade animations MUST honor
`prefers-reduced-motion: reduce`:

- Slide animations replaced with instant appearance.
- Backdrop fade replaced with instant opacity.
- The Accessibility contract is unchanged (focus trap, return-focus, etc.
  still apply); only the visual transition is suppressed.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Right vs. bottom default on mobile.** Right-anchored drawers on
   small viewports require thumb stretch; bottom-anchored fits modern
   mobile patterns. Contract defaults to right (matches shadcn Sheet);
   consumer chooses per surface.
2. **Resizable drawer.** Drag-to-resize handle on the inner edge.
   OUT OF M2 SCOPE; could be a M3 enhancement.
3. **Multi-step drawer (wizard inside).** Drawer hosts arbitrary content,
   so a multi-step flow is the consumer's composition; no contract
   change. The drawer simply needs enough height/width.
4. **Drawer-within-drawer.** Permissible but discouraged; nested overlays
   are SR-disorienting. Contract permits via Radix's standard nested-
   dialog support but flags as a discouraged pattern.
5. **`overlays.tokens.json` file ownership.** The M1 PAO Batch C contracts
   proposed this file; this M2 PR is its first concrete authoring (M1
   noted the file as "proposed; follow-on PR"). If the M1 follow-on
   token-file PR is filed BEFORE this lands, that PR creates the file
   and this PR amends; otherwise this PR creates and the M1 follow-on
   becomes an amend. Coordinate with Admiral on merge ordering.

---

## 8. Do / Don't

### Do

- Use Radix Dialog primitives via shadcn Sheet pattern — they handle the
  focus trap + Escape + return-focus.
- Pin the side via prop; the data-attribute pattern animates correctly.
- Pair `--sf-drawer-*` tokens with WCAG-compliant contrast.
- Use `data-state=open|closed` for entrance/exit animations.
- Honor `prefers-reduced-motion` on slide + fade.

### Don't

- Don't put hex values in component code. Component CSS consumes
  `var(--sf-drawer-*)` only.
- Don't nest drawers more than one deep. SR users get disoriented.
- Don't ship a drawer without a close button. Even with Escape + scrim,
  the explicit Close affordance is mandatory.
- Don't drop body-scroll lock during drawer-open (per Accessibility).
- Don't auto-focus the drawer's first interactive element if it's a
  destructive control (e.g., a "Delete" button) — default initial-focus
  per Dialog convention (the close button or the first non-destructive
  control).

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Sheet on Radix Dialog.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-side-panel>` or
  similar; tokens are framework-neutral.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #46 Drawer / Sheet (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Sheet — primary React foundation
- Radix Dialog — underlying primitive
- [Drawer.Semantic.md](./Drawer.Semantic.md) — prop contract
- [Drawer.Interaction.md](./Drawer.Interaction.md) — behavioural contract
- [Drawer.Accessibility.md](./Drawer.Accessibility.md) — focus trap, return-focus, scroll-lock
- [Dialog.Styling.md](./Dialog.Styling.md) — companion overlay
- [Dialog.Accessibility.md](./Dialog.Accessibility.md) — inherited focus-trap requirements
- `_shared/design/tokens/overlays.tokens.json` (proposed) — default token values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
