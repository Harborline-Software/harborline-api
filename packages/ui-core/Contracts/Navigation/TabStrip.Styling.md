# TabStrip — Styling Contract

- **Component:** TabStrip
- **ADR 0017 family:** Navigation
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TabStrip.Semantic.md) · [Interaction](./TabStrip.Interaction.md) · [Accessibility](./TabStrip.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Radix Tabs + shadcn Tabs_
- **Catalog row:** #131 TabStrip (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Tabs`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

TabStrip is a tab-list + tab-panel composition used to switch between
mutually-exclusive views within a single page region (e.g., Property →
Overview / Leases / Maintenance / Documents). It is distinct from SideNav
(which navigates URLs) and from a Stepper (which guides a sequence).

This contract names the `--sf-tabstrip-*` token surface (orientation,
variant, size, state) and pins the Tailwind class recipes for the M2
default layer. TabStrip composes on Radix Tabs primitives.

---

## 2. Token surface

TabStrip exposes the following CSS custom properties (the `--sf-tabstrip-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Container tokens

| Token | Semantic role | Varies | Notes |
|---|---|---|---|
| `--sf-tabstrip-bg` | TabStrip container background | theme | Pairs with active-tab foreground at ≥ 4.5:1 |
| `--sf-tabstrip-border-bottom` | Bottom border (separates tab strip from tab panel) | theme | Pairs with `--sf-tabstrip-bg` at ≥ 3:1; the active tab "lifts" above this border |
| `--sf-tabstrip-indicator-color` | Active-tab underline / left-stripe colour | theme | Brand-accent; carries the cross-channel current-tab signal alongside `aria-selected` |
| `--sf-tabstrip-gap` | Horizontal gap between adjacent tabs | none | typically `0` (tabs are flush with the underline indicator) or `0.25rem` (small visual separation) |

### 2.2 Tab-state tokens

Tabs have five states: default / hover / active (selected) / focus-visible / disabled.

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-tabstrip-tab-bg-{state}` | Tab background | yes | typically `transparent` for all states EXCEPT `pill` variant; the underline `default` variant uses `bg-transparent` across states |
| `--sf-tabstrip-tab-fg-{state}` | Tab foreground (label + icon) | yes | active/selected: brand-accent; default: muted neutral; pairs with `--sf-tabstrip-bg` at ≥ 4.5:1 |
| `--sf-tabstrip-tab-border-bottom-{state}` | Tab bottom border (underline indicator) | yes (default variant) | `default` state: `transparent`; `active` state: `--sf-tabstrip-indicator-color`; 2-3px |
| `--sf-tabstrip-tab-padding-y-{size}` | Tab vertical padding | size | sm/md/lg |
| `--sf-tabstrip-tab-padding-x-{size}` | Tab horizontal padding | size | sm/md/lg |
| `--sf-tabstrip-tab-font-size-{size}` | Tab label font size | size | sm/md/lg |
| `--sf-tabstrip-tab-icon-size-{size}` | Tab icon size (when present) | size | sm/md/lg |
| `--sf-tabstrip-tab-icon-gap` | Icon-to-label gap | none | typically `0.5rem` |
| `--sf-tabstrip-tab-min-height` | Tab touch-target min-height | size | MUST be ≥ 32px on sm, 40px on md, 44px on lg per SC 2.5.8 + Spacing Exception |
| `--sf-tabstrip-tab-radius` | Tab corner radius | variant | `default` variant: `0` (flush); `pill` variant: `9999px` (rounded); `card` variant: top corners rounded only |

### 2.3 Variant axis

Three style variants. Engineer chooses per surface context.

| Variant | Indicator type | Use case |
|---|---|---|
| `underline` (default) | Bottom-border accent on active tab | Default for page-level tab regions |
| `pill` | Rounded background fill on active tab | Compact tab clusters inside cards / toolbars |
| `card` | Active tab "lifts" with top-rounded corners; bg-blends with container below | Card-internal sectioning |

The `--sf-tabstrip-tab-*` tokens are variant-aware. Tokens for non-default
variants live alongside the default tokens in the `navigation.tokens.json`
file under `tabStrip.variant.{pill|card}`.

### 2.4 Orientation axis

| Orientation | Behaviour |
|---|---|
| `horizontal` (default) | Tabs arranged left-to-right; indicator is a bottom border |
| `vertical` | Tabs arranged top-to-bottom; indicator is a left border |

Vertical orientation rotates the indicator to a left-stripe and pivots
keyboard arrow handling from Left/Right to Up/Down (per Accessibility §5).

### 2.5 Token additions to `navigation.tokens.json`

Recommended `tabStrip` namespace:

```jsonc
"tabStrip": {
  "_intent": "tab-list + tab-panel composition; three variants × two orientations × three sizes; current-tab signal via aria-selected + indicator stripe + fg-accent",
  "bg": "transparent",
  "borderBottom": "#E5E7EB",
  "indicatorColor": "#2563EB",
  "gap": "0",
  "variant": {
    "underline": {
      "tabBgDefault": "transparent",
      "tabBgHover": "transparent",
      "tabBgActive": "transparent",
      "tabBgDisabled": "transparent",
      "tabFgDefault": "#6B7280",
      "tabFgHover": "#111827",
      "tabFgActive": "#1D4ED8",
      "tabFgDisabled": "#D1D5DB",
      "tabBorderBottomDefault": "transparent",
      "tabBorderBottomHover": "transparent",
      "tabBorderBottomActive": "#2563EB",
      "tabBorderBottomDisabled": "transparent",
      "tabRadius": "0"
    },
    "pill": {
      "tabBgDefault": "transparent",
      "tabBgHover": "#F3F4F6",
      "tabBgActive": "#2563EB",
      "tabBgDisabled": "transparent",
      "tabFgDefault": "#374151",
      "tabFgHover": "#111827",
      "tabFgActive": "#FFFFFF",
      "tabFgDisabled": "#9CA3AF",
      "tabBorderBottomDefault": "transparent",
      "tabBorderBottomHover": "transparent",
      "tabBorderBottomActive": "transparent",
      "tabBorderBottomDisabled": "transparent",
      "tabRadius": "9999px"
    },
    "card": {
      "tabBgDefault": "#F9FAFB",
      "tabBgHover": "#F3F4F6",
      "tabBgActive": "#FFFFFF",
      "tabBgDisabled": "#F9FAFB",
      "tabFgDefault": "#6B7280",
      "tabFgHover": "#111827",
      "tabFgActive": "#111827",
      "tabFgDisabled": "#D1D5DB",
      "tabBorderBottomDefault": "transparent",
      "tabBorderBottomHover": "transparent",
      "tabBorderBottomActive": "transparent",
      "tabBorderBottomDisabled": "transparent",
      "tabRadius": "0.375rem 0.375rem 0 0"
    }
  },
  "size": {
    "sm": { "paddingY": "0.375rem", "paddingX": "0.75rem", "fontSize": "0.75rem",   "iconSize": "0.875rem", "minHeight": "2rem" },
    "md": { "paddingY": "0.5rem",   "paddingX": "1rem",    "fontSize": "0.875rem",  "iconSize": "1rem",     "minHeight": "2.5rem" },
    "lg": { "paddingY": "0.625rem", "paddingX": "1.25rem", "fontSize": "1rem",      "iconSize": "1.25rem",  "minHeight": "2.75rem" }
  },
  "iconGap": "0.5rem",
  "_notes": {
    "contrast": "Every variant's tabFg/tabBg pair verified ≥ 4.5:1 per WCAG 2.2 SC 1.4.3. Indicator on bg ≥ 3:1 per WCAG 2.2 SC 1.4.11 (so the underline stripe is perceivable on its own — required for the cross-channel signal).",
    "activeChannelCount": "Active tab signaled by THREE channels: (1) tabFg-active (brand-accent), (2) indicator stripe (border-bottom or pill bg), (3) aria-selected='true' (Accessibility). For pill variant, tabBg-active also carries; FOUR channels.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Underline variant (default)

```jsx
<div role="tablist" aria-label="Property details" class="flex border-b border-gray-200">
  <button role="tab" aria-selected="true" aria-controls="panel-overview" id="tab-overview"
          class="px-4 py-2 text-sm font-medium text-blue-700 border-b-2 border-blue-600 -mb-px
                 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 focus-visible:ring-offset-2">
    Overview
  </button>
  <button role="tab" aria-selected="false" aria-controls="panel-leases" id="tab-leases"
          class="px-4 py-2 text-sm font-medium text-gray-500 hover:text-gray-900 border-b-2 border-transparent
                 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 focus-visible:ring-offset-2">
    Leases
  </button>
</div>
<div role="tabpanel" id="panel-overview" aria-labelledby="tab-overview" class="pt-4">...</div>
<div role="tabpanel" id="panel-leases" aria-labelledby="tab-leases" class="pt-4 hidden">...</div>
```

The `-mb-px` on the active tab's class is critical: it offsets the active
tab's bottom border DOWN by 1px so the active border PAINTS OVER the
container's `border-b` — visually "lifting" the active tab onto the strip.

### 3.2 Pill variant

```jsx
<div role="tablist" class="inline-flex gap-1 rounded-lg bg-gray-100 p-1">
  <button role="tab" aria-selected="true" class="px-3 py-1.5 text-sm rounded-md bg-blue-600 text-white">
    Today
  </button>
  <button role="tab" aria-selected="false" class="px-3 py-1.5 text-sm rounded-md text-gray-700 hover:bg-gray-200">
    Week
  </button>
</div>
```

### 3.3 Card variant

Same structure as underline variant but tabs have `rounded-t-md bg-gray-50`
default; active tab has `bg-white` matching the panel below — the tab
appears connected to the panel.

### 3.4 Vertical orientation

```jsx
<div class="flex">
  <div role="tablist" aria-orientation="vertical" class="flex flex-col border-r border-gray-200 pr-px">
    <button role="tab" aria-selected="true"
            class="px-4 py-2 text-sm text-left text-blue-700 border-r-2 border-blue-600 -mr-px">
      Overview
    </button>
    {/* ... */}
  </div>
  <div role="tabpanel" class="flex-1 pl-4">...</div>
</div>
```

### 3.5 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer.

---

## 4. Visual state inventory

Per-tab state matrix:

| State | Trigger | Tokens (underline variant) | Recipe |
|---|---|---|---|
| **default** | inactive, idle | `tabFg-default` = muted neutral; `tabBorderBottom-default` = transparent | `text-gray-500 border-b-2 border-transparent` |
| **hover** | pointer over inactive tab | `tabFg-hover` = stronger neutral | `hover:text-gray-900` |
| **active / selected** | `aria-selected="true"` | `tabFg-active` = brand-accent; `tabBorderBottom-active` = brand-accent | `aria-[selected=true]:text-blue-700 aria-[selected=true]:border-blue-600` |
| **focus-visible** | keyboard focus | focus ring | `focus-visible:ring-2 focus-visible:ring-blue-600` |
| **disabled** | `aria-disabled="true"` on tab | reduced opacity | rare; tab is non-selectable but visible |

**State precedence (highest first):** disabled → active → focus-visible
(additive) → hover → default.

---

## 5. Responsive behaviour

Horizontal TabStrip overflow:

| Overflow strategy | Behaviour |
|---|---|
| Scroll (default) | Container `overflow-x-auto`; tabs scroll horizontally on narrow viewports; scroll position is managed by the consumer (or by the implementation library) |
| Stack | Tabs wrap to multiple lines; the underline indicator follows |
| More-menu | Excess tabs collapse to a "More" dropdown |

The contract pins SCROLL as the default; stack and more-menu are opt-in
strategies (Semantic contract owns the prop).

Vertical TabStrip naturally accommodates many tabs (it grows downward).
Overflow is `overflow-y-auto` on the tablist container if needed.

---

## 6. Reduced motion

If the underline-indicator slides between tabs on selection change (the
common shadcn / Radix animation), this transition MUST honor
`prefers-reduced-motion: reduce`:

- Default: `transition-all duration-200` on the indicator's position
  (typically rendered as a separate animated element).
- Reduced-motion: `motion-reduce:transition-none` — indicator jumps
  to the new position instantly.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Indicator animation: separate sliding stripe vs. per-tab border.**
   shadcn / Radix typically uses a single animated stripe that slides
   under the active tab. The recipe above uses per-tab `border-bottom`
   which is simpler + has no animation. The contract permits BOTH; the
   token surface stays the same regardless.
2. **Automatic activation vs. manual activation.** Radix Tabs supports
   both: `automatic` (arrow keys activate AND focus); `manual` (arrow
   keys focus, Space/Enter activates). Accessibility §5 discusses; the
   styling implications are nil (both render the same focused vs.
   selected combination).
3. **Overflow strategy default.** Scroll vs. wrap vs. more-menu. Default
   pinned at SCROLL because it preserves tab order and visibility
   discoverability; council may override.
4. **Selected-tab indicator alignment with text.** Some designs align the
   indicator stripe with the tab's text content (narrower than the tab
   button); the contract default aligns with the FULL tab width. Both
   are acceptable; engineer choice.
5. **Disabled tab visibility.** Disabled tabs are rare but used for
   "available in next release" or "requires permission" patterns. Render
   with `opacity-50` + tooltip; alternatively, hide entirely. Default:
   render visible with `aria-disabled="true"` so the user knows the
   tab exists.

---

## 8. Do / Don't

### Do

- Use THREE-channel current-tab signal: `aria-selected` + brand-accent
  foreground + indicator stripe.
- Apply `-mb-px` to active tabs in underline variant so the active
  border paints over the container's border.
- Use `focus-visible:` not `focus:` so pointer-focus doesn't show ring.
- Honor `prefers-reduced-motion` on indicator slide.
- Use `aria-disabled` for permanently-disabled tabs; hide tabs that
  never apply.

### Don't

- Don't use colour alone for current-tab — pair with `aria-selected` +
  indicator stripe.
- Don't put hex values in component code. Component CSS consumes
  `var(--sf-tabstrip-*)` only.
- Don't ship more than 7-9 tabs in a horizontal strip without an
  overflow strategy.
- Don't drop the tab's `aria-controls` linkage — tabs MUST point to their
  panel by id (Accessibility §3).
- Don't bind Tab key as next-tab — Tab MOVES focus OUT of the tab list
  (Accessibility §5). Arrow keys are the in-list navigation.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** Radix Tabs primitives wrapped
  with brand styling.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-tabs>` is the WC
  reference; tokens are framework-neutral.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #131 TabStrip (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- Radix Tabs — primary React foundation
- shadcn Tabs — composition reference
- Vaadin Tabs — WC foundation
- [TabStrip.Semantic.md](./TabStrip.Semantic.md) — prop contract
- [TabStrip.Interaction.md](./TabStrip.Interaction.md) — behavioural contract
- [TabStrip.Accessibility.md](./TabStrip.Accessibility.md) — tablist / tab / tabpanel pattern
- `_shared/design/tokens/navigation.tokens.json` (proposed) — default token values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
