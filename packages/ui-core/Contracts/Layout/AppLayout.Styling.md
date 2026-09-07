# AppLayout — Styling Contract

- **Component:** AppLayout
- **ADR 0017 family:** Layout
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppLayout.Semantic.md) · [Interaction](./AppLayout.Interaction.md) · [Accessibility](./AppLayout.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Vaadin AppLayout_
- **Catalog row:** A10 AppLayout / Shell (`app-priority: high`, `library-scope: v1`, source: Vaadin AppLayout)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

AppLayout is the application shell: the top-level layout container that
composes `AppBar` (top), `SideNav` (left rail, collapsible / mobile-drawer),
and `main` content (centre). It owns the responsive geometry — how the
SideNav rail collapses to a drawer on mobile, how the main content offsets
for the rail's width, how the sticky AppBar interacts with the scrollable
main region.

This contract names the `--sf-applayout-*` token surface (dimensions, gap,
breakpoints) and pins the Tailwind class recipes for the M2 default
authoring layer. AppLayout has no visual chrome of its own — it is pure
geometry.

---

## 2. Token surface

AppLayout exposes the following CSS custom properties (the
`--sf-applayout-*` family). Adapters MUST consume these tokens; adapters
MUST NOT hard-code values in component code.

### 2.1 Geometry tokens

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-applayout-appbar-height` | Height reserved for the AppBar slot | Mirrors `--sf-appbar-height`; typically resolves via `var(--sf-appbar-height)` for cross-component consistency |
| `--sf-applayout-sidenav-width-expanded` | SideNav rail width when expanded | Typically `16rem` (256px); mirrors `--sf-sidenav-width-expanded` |
| `--sf-applayout-sidenav-width-collapsed` | SideNav rail width when collapsed to icon-only | Typically `4rem` (64px); mirrors `--sf-sidenav-width-collapsed` |
| `--sf-applayout-content-padding-x` | Horizontal padding inside the main content region | Typically `1rem` mobile / `2rem` desktop |
| `--sf-applayout-content-padding-y` | Vertical padding inside the main content region | Typically `1.5rem` mobile / `2rem` desktop |
| `--sf-applayout-content-max-width` | Optional max-width for the main content region | Default `none`; consumer may set to `80rem` for centred-column layouts |
| `--sf-applayout-bg` | Page background colour (behind the layout) | Typically `--sf-color-surface-muted` to provide contrast against Card / AppBar surfaces |

### 2.2 Breakpoint tokens

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-applayout-breakpoint-drawer` | Viewport width AT or BELOW which SideNav switches from rail to overlay drawer | Typically `768px` (`md` Tailwind breakpoint) |
| `--sf-applayout-breakpoint-collapsed` | Viewport width AT or BELOW which an expanded SideNav auto-collapses to icon-only | Typically `1024px` (`lg` Tailwind breakpoint) |

Breakpoint tokens are RESOLVED at CSS media-query time; they do NOT vary
by theme.

### 2.3 Token additions to `layout.tokens.json`

The `--sf-applayout-*` family extends the `layout.tokens.json` file
(created for the Card contract in M2 Batch A). The actual JSON addition
ships in a **follow-on token-file PR** after both M2 Batch A (Card; creates
the file) and M2 Batch B (AppLayout; this contract) merge — this avoids
cross-PR merge conflicts on the shared file. The contract below is the
source of truth for the eventual JSON addition:

```jsonc
"appLayout": {
  "_intent": "shell geometry — composes AppBar + SideNav + main; no visual chrome of its own. Tokens mirror the consumed child-component tokens for consistency.",
  "appBarHeight": "3.5rem",
  "sideNavWidthExpanded": "16rem",
  "sideNavWidthCollapsed": "4rem",
  "contentPaddingXMobile": "1rem",
  "contentPaddingXDesktop": "2rem",
  "contentPaddingYMobile": "1.5rem",
  "contentPaddingYDesktop": "2rem",
  "contentMaxWidth": "none",
  "bg": "#F9FAFB",
  "breakpoint": {
    "drawer": "768px",
    "collapsed": "1024px"
  },
  "_notes": {
    "tokenMirroring": "appBarHeight, sideNavWidthExpanded, sideNavWidthCollapsed mirror the same tokens in appBar/sideNav namespaces — they exist here so AppLayout can compute geometry without taking a hard dependency on those namespaces being loaded. Consumers MAY override either side independently but MUST keep them in sync; otherwise the rail width and the content offset disagree.",
    "responsiveContentPadding": "Two padding tokens (Mobile / Desktop) is the contract default. Adapters MAY resolve via a CSS @media query OR a CSS container query; either is acceptable.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Shell composition

```jsx
{/* CORRECT — body-scroll model (AppBar sticky works) */}
<div class="flex min-h-screen flex-col bg-gray-50">
  <AppBar />  {/* sticky top-0 — works because body is the scroll container */}
  <div class="flex flex-1">
    <SideNav />
    <main id="main" class="flex-1 px-4 py-6 lg:px-8 lg:py-8">
      {/* page content — body scrolls, not main */}
    </main>
  </div>
</div>
```

**⚠ Do NOT put `overflow-y-auto` on `<main>`.** If `<main>` scrolls instead of `<body>`, `position: sticky` on AppBar has no effect — AppBar is outside `<main>`'s scroll context and behaves as `position: relative`. This is SI3-2 (see §5 Known gaps).

```jsx
{/* WRONG — inner-scroll model (AppBar sticky broken) */}
<div class="flex min-h-screen flex-col">
  <AppBar />  {/* sticky does NOT work — sibling of scroll container */}
  <div class="flex flex-1 overflow-hidden">
    <SideNav />
    <main class="flex-1 overflow-y-auto">  {/* ← this makes main the scroll container */}
    </main>
  </div>
</div>
```

**`headerFixed` variant** (use when you need inner-scroll on `<main>`): Set `headerFixed={true}` on AppBar to use `position: fixed; top: 0` instead of sticky. The outer shell then adds `padding-top: var(--sf-appbar-height)` to prevent content rendering under the fixed bar.

### 3.2 Layout regions

| Region | Tailwind recipe (body-scroll model) |
|---|---|
| Outer shell | `flex min-h-screen flex-col bg-gray-50` |
| Body row (SideNav + main) | `flex flex-1` (no `overflow-hidden` — body scrolls) |
| Main content region | `flex-1 px-4 py-6 lg:px-8 lg:py-8` (no `overflow-y-auto`) |
| Sticky AppBar wrapper | `sticky top-0 z-30` (applied at the AppBar's host, not inside AppBar itself) |
| SideNav rail (desktop) | `hidden md:flex w-64 shrink-0` |
| SideNav drawer (mobile) | `fixed inset-y-0 left-0 z-40 w-64 transform transition-transform` + `translate-x-0` / `-translate-x-full` for open/closed |
| Drawer scrim (mobile) | `fixed inset-0 z-30 bg-black/50` (shown only when drawer is open) |

### 3.3 Z-index strata

Three explicit z-index layers:

| Layer | z-index | Tailwind class |
|---|---|---|
| Sticky AppBar | 30 | `z-30` |
| Drawer scrim (mobile) | 30 | `z-30` |
| Drawer panel (mobile) | 40 | `z-40` |
| Dialog overlay (composed inside main content) | 50 | `z-50` (Dialog contract) |
| Toast / Notification stack | 60 | `z-[60]` (Notification contract; Tailwind v3 arbitrary value — `z-60` is not a default utility class) |

The layout reserves 30-49 for shell chrome; higher layers (50+) belong to
overlay components.

### 3.4 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer. A consumer
who wants to re-theme AppLayout consumes the **tokens**, not the Tailwind
classes.

---

## 4. Visual state inventory

AppLayout has two layout states that affect its child geometry:

| State | Trigger | Visual effect |
|---|---|---|
| **rail-expanded** | viewport ≥ `lg` (≥ 1024px) AND consumer's expanded prop is true | SideNav shows full-width rail with labels; main content offsets by `--sf-applayout-sidenav-width-expanded` |
| **rail-collapsed** | viewport ≥ `md` AND < `lg`, OR consumer's expanded prop is false | SideNav shows icon-only rail; main content offsets by `--sf-applayout-sidenav-width-collapsed` |
| **drawer-closed** | viewport < `md` (mobile) | SideNav is offscreen; main content takes full width; hamburger toggle appears in AppBar |
| **drawer-open** | viewport < `md` AND hamburger toggle activated | SideNav slides in from left as overlay drawer; scrim overlays main content; body scroll locked |

The four states are mutually exclusive at any given viewport / control
combination. Transitions between rail states are visual-only (no layout
reflow other than the rail width change); transitions between drawer
states animate via `transform: translateX(...)`.

---

## 5. Responsive behaviour

AppLayout's responsive behaviour is the central concern of this contract.

| Viewport | SideNav behaviour | Main content |
|---|---|---|
| Mobile (< 768px) | Hidden by default; opens as overlay drawer via AppBar hamburger toggle | Full width; scrolls under sticky AppBar |
| Tablet (768px – 1024px) | Icon-only collapsed rail (4rem wide); hover/focus may expand temporarily | Offset by 4rem |
| Desktop (≥ 1024px) | Expanded rail (16rem wide) by default; user may toggle to collapsed | Offset by 4rem or 16rem |

**Sticky AppBar.** AppBar uses `position: sticky; top: 0; z-index: 30`. CSS sticky only sticks relative to its nearest scrolling ancestor — if AppBar is OUTSIDE a scroll container (e.g., sibling of `overflow-y: auto` main), it behaves as `position: relative` and does not stick.

**Correct sticky geometry:** the outer shell (`flex min-h-screen flex-col`) MUST NOT have `overflow: hidden` or `overflow: auto`. Page scrolling must happen on `<body>` or `<html>` — not on the shell div. With body-scroll, AppBar is inside the body's scroll ancestor and `sticky top-0` works correctly.

**Alternative (`headerFixed` prop):** AppBar uses `position: fixed; top: 0; left: 0; right: 0; height: var(--sf-appbar-height)`. In this mode the outer shell MUST add `padding-top: var(--sf-appbar-height)` to prevent content from rendering under the fixed bar.

**Drawer body-scroll lock.** When the mobile drawer is open, the body's
scroll MUST be locked (`overflow: hidden` on `<body>`) to prevent the
page scrolling under the open drawer. The drawer itself remains
scrollable internally. AppLayout's implementation OWNS this scroll-lock
behaviour (it's a layout-level concern, not a SideNav concern).

---

## 6. Reduced motion

The mobile drawer's slide-in/slide-out animation MUST honor
`prefers-reduced-motion: reduce`:

- Default: `transition-transform duration-200` on the drawer panel.
- Reduced-motion: `motion-reduce:transition-none` — drawer appears /
  disappears without animation.

The scrim's fade-in/fade-out animation MUST also honor reduced motion.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **SideNav position (left vs. right).** Default is left. RTL locales
   typically flip to right. The token surface uses logical `inset-inline-
   start` (CSS logical properties) where supported; Tailwind utility
   classes fall back to `left-0` (LTR) with `rtl:right-0` override.
2. **Multiple rails.** Some applications need both a left primary nav
   AND a right context panel (chat, notifications, properties pane).
   AppLayout M2 supports left rail only. Right-rail composition is a
   M3 enhancement.
3. **Header beneath AppBar (sub-nav).** A common pattern places a tab
   strip or breadcrumb row beneath the AppBar. AppLayout does not pin a
   slot for this; the consumer composes within `<main>` at the top.
   Alternatively, a future enhancement could add a `subBar` slot.
4. **Footer.** AppLayout does NOT include a footer slot at M2. Most
   web applications don't use a persistent footer; pages that need one
   render it inside their own page content.
5. **Page background.** The contract default is a muted neutral
   (`--sf-color-surface-muted` → `#F9FAFB`). Some apps prefer pure
   white. Provider override; not in M2 scope.

---

## 8. Do / Don't

### Do

- Compose AppBar at top, SideNav left, main content right — that
  three-region layout is the contract.
- Use the four state model (rail-expanded / rail-collapsed / drawer-
  closed / drawer-open) and pick state via viewport + consumer prop.
- Lock body scroll when the drawer is open.
- Apply `scroll-margin-top: var(--sf-appbar-height)` to elements inside
  `<main>` so focused content clears the sticky AppBar.
- Use the documented z-index strata (30 chrome / 40 drawer / 50 dialog
  / 60 toast).

### Don't

- Don't put hex values or magic numbers in component code. Consume
  `var(--sf-applayout-*)` tokens.
- Don't nest AppLayout inside another layout — there's one app shell
  per application.
- Don't hard-code z-index outside the documented strata.
- Don't ship a drawer that doesn't honor `prefers-reduced-motion`.
- Don't let the drawer scrim swallow Escape-key handling — Escape
  belongs to the SideNav drawer behaviour (close drawer).

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** Vaadin AppLayout pattern adapted
  to React.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-app-layout>` is
  the direct foundation; the WC track may use it nearly verbatim.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog A10 AppLayout / Shell (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- Vaadin AppLayout — primary reference foundation
- [AppLayout.Semantic.md](./AppLayout.Semantic.md) — prop contract
- [AppLayout.Interaction.md](./AppLayout.Interaction.md) — behavioural contract
- [AppLayout.Accessibility.md](./AppLayout.Accessibility.md) — landmark composition
- [AppBar.Styling.md](../Navigation/AppBar.Styling.md) — child contract
- [SideNav.Styling.md](../Navigation/SideNav.Styling.md) — child contract
- `_shared/design/tokens/layout.tokens.json` — default token values
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum)
