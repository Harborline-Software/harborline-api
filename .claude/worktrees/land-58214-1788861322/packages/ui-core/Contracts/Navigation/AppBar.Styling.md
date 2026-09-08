# AppBar — Styling Contract

- **Component:** AppBar
- **ADR 0017 family:** Navigation
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppBar.Semantic.md) · [Interaction](./AppBar.Interaction.md) · [Accessibility](./AppBar.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Vaadin AppLayout's app-bar slot pattern_
- **Catalog row:** #4 AppBar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

AppBar is the top-of-application chrome bar: typically holds brand mark
(left), primary navigation or page-title region (centre/left-of-centre),
and identity / utility actions (right). It is a structural primitive (no
business logic) but it carries the **landmark `role="banner"`** that
anchors the application's accessibility outline.

This contract names the `--sf-appbar-*` token surface (chrome — height,
background, foreground, border, shadow, padding, slot rhythm) and pins the
Tailwind class recipes that act as the M2 default styling layer.

---

## 2. Token surface

AppBar exposes the following CSS custom properties (the `--sf-appbar-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Chrome tokens (variant-axis: `flat` | `bordered` | `elevated`)

Three visual variants control border + shadow treatment.

| Token | Semantic role | Varies by | Notes |
|---|---|---|---|
| `--sf-appbar-bg-{variant}` | Background fill per variant | variant + theme | `flat`/`bordered`: neutral surface (typically white). `elevated`: same; shadow carries the separation. Provider themes may resolve to brand colour. |
| `--sf-appbar-fg` | Default foreground (brand text, page title, action labels) | theme | Pairs with `--sf-appbar-bg-{variant}` at ≥ 4.5:1 |
| `--sf-appbar-border-bottom-{variant}` | Bottom border colour per variant | variant + theme | `flat`: `transparent`. `bordered`: `--sf-color-border` (≥ 3:1 per WCAG 2.2 SC 1.4.11). `elevated`: `transparent`. |
| `--sf-appbar-border-width-{variant}` | Bottom border thickness per variant | variant | `flat` / `elevated`: `0`. `bordered`: `1px`. |
| `--sf-appbar-shadow-{variant}` | Drop shadow per variant | variant | `flat` / `bordered`: `none`. `elevated`: subtle 1-px shadow. |
| `--sf-appbar-height-{size}` | Total bar height per size | size axis | `compact`: `3rem` (48px) / `default`: `3.5rem` (56px) / `large`: `4rem` (64px); see §2.3 |
| `--sf-appbar-padding-x` | Horizontal padding (left edge to first slot; last slot to right edge) | none | Typically `1rem` (`px-4`) on mobile, `1.5rem` (`px-6`) on desktop — responsive via media query in §5 |
| `--sf-appbar-slot-gap` | Horizontal gap between adjacent slots / items | none | Typically `0.75rem` (`gap-3`) |
| `--sf-appbar-radius` | Corner radius (if AppBar is rendered as a floating card) | none | Default `0` (AppBar spans full width); only set when consumer wraps AppBar in a floating-shell pattern |

### 2.2 Brand-mark slot tokens

| Token | Semantic role | Varies by | Notes |
|---|---|---|---|
| `--sf-appbar-brand-fg` | Foreground colour for the brand wordmark | theme | Typically resolves to `--sf-appbar-fg`; may diverge for a tinted-accent wordmark |
| `--sf-appbar-brand-font-size` | Font size for the wordmark | size axis | Typically `text-lg` for `default` / `text-xl` for `large` |
| `--sf-appbar-brand-font-weight` | Font weight | none | Typically `600` (`font-semibold`) |
| `--sf-appbar-brand-icon-size` | Logo glyph dimensions | size axis | Square, derived from `--sf-appbar-height` minus padding |

### 2.3 Size axis

Three sizes. Most applications pick one and stick with it.

| Size | Height | Use case |
|---|---|---|
| `compact` | 48px | Compact / dense applications |
| `default` | 56px | Default for most applications (matches Vaadin AppLayout default) |
| `large` | 64px | Brand-heavy applications; ample brand-mark space |

### 2.4 Token additions to `navigation.tokens.json` (NEW)

The `--sf-appbar-*` family lives in a new `navigation.tokens.json` file
under `_shared/design/tokens/`. The file holds AppBar + SideNav + TabStrip
namespaces (Navigation family).

Recommended initial structure for the `appBar` namespace:

```jsonc
"appBar": {
  "_intent": "top-of-application landmark; chrome only — no menu / dropdown behaviour belongs here",
  "fg": "#111827",
  "paddingX": "1.5rem",
  "slotGap": "0.75rem",
  "radius": "0",
  "variant": {
    "flat":     { "bg": "#FFFFFF", "borderBottom": "transparent", "borderWidth": "0", "shadow": "none" },
    "bordered": { "bg": "#FFFFFF", "borderBottom": "#E5E7EB",    "borderWidth": "1px", "shadow": "none" },
    "elevated": { "bg": "#FFFFFF", "borderBottom": "transparent", "borderWidth": "0", "shadow": "0 1px 2px rgba(17, 24, 39, 0.04)" }
  },
  "size": {
    "compact": { "height": "3rem",    "brandFontSize": "1rem",     "brandIconSize": "1.5rem" },
    "default": { "height": "3.5rem",  "brandFontSize": "1.125rem", "brandIconSize": "1.75rem" },
    "large":   { "height": "4rem",    "brandFontSize": "1.25rem",  "brandIconSize": "2rem" }
  },
  "brand": {
    "fg": "#111827",
    "fontWeight": 600
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Base recipe

```
flex items-center justify-between gap-3 w-full bg-white text-gray-900
border-b border-gray-200 px-4 sm:px-6
```

### 3.2 Size recipes

| Size | Height recipe |
|---|---|
| `compact` | `h-12` |
| `default` | `h-14` |
| `large` | `h-16` |

### 3.3 Slot composition

AppBar has three named regions:

| Slot | Position | Tailwind recipe |
|---|---|---|
| `start` | Left edge — brand mark, optional menu toggle (mobile) | `flex items-center gap-3` |
| `center` | Centre / left-of-centre — page title, primary nav (desktop) | `flex items-center gap-3 flex-1 min-w-0` (`min-w-0` enables truncation on overflow) |
| `end` | Right edge — utility actions, identity menu | `flex items-center gap-2` |

Composition:

```jsx
<AppBar size="default">
  <AppBar.Start>
    <Brand />
  </AppBar.Start>
  <AppBar.Center>
    <PageTitle />
  </AppBar.Center>
  <AppBar.End>
    <NotificationsButton />
    <ProfileMenu />
  </AppBar.End>
</AppBar>
```

The `center` slot's `flex-1 min-w-0` lets the title region grow to fill
available space and truncate when narrow viewports compress the centre
between large `start`/`end` content.

### 3.4 Skip-nav anchor

Per Accessibility §3, AppBar SHOULD include a visually-hidden skip-nav
link as the FIRST focusable child:

```jsx
<a href="#main" class="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:rounded focus:bg-blue-600 focus:px-3 focus:py-1 focus:text-white">
  Skip to main content
</a>
```

The skip-nav target (`#main`) MUST exist on the page; consumer responsibility.

### 3.5 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer.

---

## 4. Visual state inventory

AppBar itself is non-interactive. Its visible state is single-state per
size variant.

| State | Trigger | Notes |
|---|---|---|
| **default** | always | Single visual state |
| **hover/focus/active** | — | NOT supported at AppBar level. Child elements (brand link, menu toggle, identity menu) carry their own interactive states |
| **disabled** | — | NOT applicable |
| **scrolled** | (optional) page scrolled below the top edge | Some designs add an elevation shift (`shadow-md`) when the page has scrolled. This is OPT-IN via prop or CSS; the contract does NOT require it but documents the recipe in §7 open questions |

---

## 5. Responsive behaviour

AppBar's responsiveness centres on the `start` / `center` / `end` slot
composition:

| Viewport | Behaviour |
|---|---|
| Mobile (< 640px) | `start` slot typically shows brand mark + menu toggle; `center` may collapse (page title only); `end` shows ≤ 2 icon-only buttons; padding reduces to `px-4` |
| Tablet (640px – 1024px) | `start` shows brand only; `center` shows page title; `end` shows identity menu + 1-2 utility buttons; padding `px-6` |
| Desktop (≥ 1024px) | `start` shows brand + optional inline nav links; `center` shows page title or primary tabs; `end` shows full action set; padding `px-6` |

The contract provides the recipe but the slot content composition is the
consumer's choice. AppBar does NOT auto-collapse content; the consumer
explicitly picks which children render at which breakpoint via Tailwind
responsive classes on their own components (`sm:hidden`, `md:flex`, etc.).

**Sticky positioning.** AppBar is typically `position: sticky; top: 0; z-50`
so it stays anchored as the page scrolls. The contract does NOT include
position tokens — the consumer applies sticky positioning at the host
layout level.

---

## 6. Reduced motion

AppBar has NO animations by default. No reduced-motion handling required.

If the consumer adds a scroll-aware elevation transition (`transition-
shadow` on the scrolled state), that MUST honor `prefers-reduced-motion:
reduce`. That responsibility lies with the consumer's stylesheet, not the
AppBar contract.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Scrolled-state elevation transition.** Some designs lift the bar
   slightly (add shadow) when the page has scrolled. This is opt-in; the
   token surface supports it via `--sf-appbar-shadow` (the consumer just
   toggles a class). Should the contract ship a standard `scrolled`
   variant token (`--sf-appbar-shadow-scrolled`)? Current: NO — keep the
   surface minimal; consumer composes.
2. **Branded background variant.** Some applications use a brand-coloured
   AppBar (e.g., navy or maroon) instead of neutral. The token surface
   supports any provider override; the default is neutral-white. A
   `branded` variant could be added if the design system surfaces a
   need; not in M2 scope.
3. **Logo height vs. bar height.** The brand-icon size MUST fit inside the
   bar with sufficient padding (typically `--sf-appbar-height` × 0.5).
   The token defaults satisfy this; consumer overrides MUST re-check.
4. **Centre-slot truncation.** When the centre slot's content (e.g., a
   long page title) exceeds available width, it MUST truncate with
   ellipsis. The recipe (`min-w-0` + `truncate` on the title element)
   is documented but not enforced at the AppBar level — the centre slot
   accepts arbitrary children.
5. **Mobile drawer menu trigger.** SideNav can be opened on mobile via a
   "hamburger" toggle inside AppBar's `start` slot. That toggle is NOT
   an AppBar concern — it's a SideNav consumer pattern. AppBar provides
   the slot, SideNav provides the toggle interaction.

---

## 8. Do / Don't

### Do

- Pair `--sf-appbar-bg` and `--sf-appbar-fg` at ≥ 4.5:1.
- Pair `--sf-appbar-border-bottom` against `--sf-appbar-bg` at ≥ 3:1
  per WCAG 2.2 SC 1.4.11.
- Use the three named slots (`start` / `center` / `end`) so the
  composition is predictable across viewports.
- Apply `position: sticky; top: 0` at the host layout — AppBar itself
  is content-positioning-agnostic.
- Include the skip-nav link as the first child (Accessibility §3).

### Don't

- Don't put hex values in component code. Component CSS consumes
  `var(--sf-appbar-*)` only.
- Don't hard-code `z-index` on AppBar — z-index is layout-specific;
  the host stacking context decides.
- Don't stuff the centre slot with multiple unrelated controls.
  Each slot has a clear role.
- Don't omit the bottom border AND the shadow — without one, the AppBar
  bleeds into page content visually.
- Don't expand height dynamically (the bar's height is contractual).
  Use breadcrumbs or sub-nav rows below the AppBar if more chrome is
  needed.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** Vaadin AppLayout pattern adapted
  to React — a header `<header role="banner">` with three slot children.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-app-layout>` is the
  primary reference; the WC track may adopt that shape directly.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #4 AppBar (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- Vaadin AppLayout (Vaadin design system) — reference foundation
- [AppBar.Semantic.md](./AppBar.Semantic.md) — prop contract
- [AppBar.Interaction.md](./AppBar.Interaction.md) — behavioural contract
- [AppBar.Accessibility.md](./AppBar.Accessibility.md) — landmark role, skip-nav
- `_shared/design/tokens/navigation.tokens.json` (proposed; NEW file) — default token values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
