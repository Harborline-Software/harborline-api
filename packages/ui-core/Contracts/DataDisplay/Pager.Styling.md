# Pager — Styling Contract

- **Component:** Pager
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pager.Semantic.md) · [Interaction](./Pager.Interaction.md) · [Accessibility](./Pager.Accessibility.md)
- **Related contracts:** [DataGrid.Styling.md](./DataGrid.Styling.md) — DataGrid composes Pager directly below it; this contract reserves a shared cell-padding rhythm so the two surfaces align visually.
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Pager.tsx`
- **Catalog row:** #93 Pager (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

Pager is a stateless display-and-emit pagination control with three visible
regions:

1. **Left cluster** — page-size selector (when supplied) + "Showing X–Y of Z"
   summary.
2. **Right cluster** — Previous / Next chevron buttons + "Page N of M" label.
3. **Top edge** — a divider that separates Pager from the surface above it
   (typically DataGrid).

This contract names the `--sf-pager-*` token surface, the visual states, the
Tailwind class recipes that act as the M1 default styling layer, and the
provider-swap discipline. The surface is new (no M0 token surface to inherit);
DataGrid §"Pager seam" reserves `--sf-grid-pager-*` but defers ownership
to the Pager contract — this contract.

---

## 2. Token surface

Pager exposes the following CSS custom properties (the `--sf-pager-*` family).
Adapters MUST consume these tokens; adapters MUST NOT hard-code values in
component code.

### 2.1 Surface

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-pager-bg` | Background fill of the Pager surface | none | Typically resolves to the same surface as the host grid (`--sf-color-surface`) so the divider is the only visual seam |
| `--sf-pager-fg` | Foreground colour for summary + page-position text | none | Pairs with `--sf-pager-bg` at ≥ 4.5:1 |
| `--sf-pager-border-top` | Top-edge divider colour | none | Pairs with `--sf-pager-bg` at ≥ 3:1; visual seam between DataGrid and Pager |
| `--sf-pager-padding` | Internal spacing of the Pager surface (vertical + horizontal) | none | Single value; typically `var(--sf-space-3) var(--sf-space-4)` (matches DataGrid cell padding rhythm at the row scale) |

### 2.2 Page-size selector

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-pager-select-bg` | Background fill of the `<select>` control | default | Native browser styling layered under this token |
| `--sf-pager-select-border` | Border colour of the `<select>` control | default + focus | Pairs with `--sf-pager-select-bg` at ≥ 3:1 |
| `--sf-pager-select-focus-border` | Border colour when the `<select>` has focus-visible | focus | Pairs with `--sf-pager-select-bg` at ≥ 3:1; typically a brand-accent like `--sf-color-primary` |
| `--sf-pager-select-radius` | Corner radius of the `<select>` control | none | Typically `var(--sf-radius-sm)` |

### 2.3 Chevron buttons

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-pager-button-fg` | Foreground colour of the chevron icon (default + enabled) | default | Pairs with `--sf-pager-button-bg-default` (transparent) at ≥ 3:1 since it's a non-text glyph |
| `--sf-pager-button-bg-hover` | Background fill on `:hover` of an enabled chevron button | hover | Typically a neutral tinted overlay (e.g., `--sf-color-neutral-100`) |
| `--sf-pager-button-fg-disabled` | Foreground colour when the chevron is disabled (at start or end of pagination) | disabled | Lower opacity / lower contrast; not subject to the 3:1 rule because disabled controls are exempt from WCAG 2.2 SC 1.4.11 |
| `--sf-pager-button-radius` | Corner radius of the chevron button hit region | none | Typically `var(--sf-radius-sm)` |

### 2.4 Token additions to a new file: `pagination.tokens.json`

The `--sf-pager-*` family is new in M1. The recommended home is a new token
file `_shared/design/tokens/pagination.tokens.json`, separate from
`data-display.tokens.json`, because Pager is framework-neutrally a
Navigation-family component (per `M0-backlog.md` row 6) that physically
composes with DataDisplay. Splitting keeps the family ownership tidy.

Suggested initial contents (derived from the shipping Tailwind classes):

```jsonc
{
  "$schema": "https://design-tokens.github.io/community-group/format/",
  "_meta": {
    "name": "pagination.tokens",
    "description": "Default design-system values for the Pager component. Composes below DataDisplay surfaces.",
    "adr": "0017-A1",
    "status": "Draft",
    "version": "0.1.0"
  },
  "pager": {
    "surface": {
      "bg": "transparent",
      "fg": "#374151",
      "borderTop": "#E5E7EB",
      "padding": "12px 16px"
    },
    "select": {
      "bg": "#FFFFFF",
      "border": "#D1D5DB",
      "focusBorder": "#3B82F6",
      "radius": "4px"
    },
    "button": {
      "fg": "#6B7280",
      "bgHover": "#F3F4F6",
      "fgDisabled": "#9CA3AF",
      "fgDisabledOpacity": 0.4,
      "radius": "4px"
    },
    "_notes": {
      "contrast": "Summary text `--sf-pager-fg` on Pager bg verified ≥ 4.5:1 (WCAG SC 1.4.3). Select border on select bg verified ≥ 3:1 (SC 1.4.11). Chevron icon `--sf-pager-button-fg` on surface verified ≥ 3:1. Disabled state is exempt from contrast minimums.",
      "alignment": "Padding is aligned to the DataGrid cell-padding rhythm so the two surfaces flow visually when composed.",
      "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply the dark-mode token sets."
    }
  }
}
```

The token-file PR MAY ship alongside this contract or in a follow-up.

---

## 3. Tailwind class recipes (M1 default layer)

The shipping React implementation uses Tailwind utility classes:

| Region | Tailwind recipe (M1 default) | Token resolution |
|---|---|---|
| Root container | `flex items-center justify-between border-t border-gray-200 px-4 py-3 sm:px-6` | `--sf-pager-border-top` + `--sf-pager-padding` |
| Left cluster | `flex items-center gap-3 text-sm text-gray-700` | `--sf-pager-fg` |
| Page-size label group | `flex items-center gap-1.5` | gap rhythm |
| `<select>` element | `rounded border border-gray-300 px-1.5 py-0.5 text-sm focus:border-blue-500 focus:outline-none` | `--sf-pager-select-bg` + `--sf-pager-select-border` + `--sf-pager-select-focus-border` + `--sf-pager-select-radius` |
| Summary text wrapper | `aria-live="polite"` on `<span>` (no extra classes) | `--sf-pager-fg` (inherited) |
| Right cluster | `flex items-center gap-1` | gap rhythm |
| Previous / Next button | `rounded p-1 text-gray-500 hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-40` | `--sf-pager-button-fg` + `--sf-pager-button-bg-hover` + `--sf-pager-button-radius` + `--sf-pager-button-fg-disabled` |
| Chevron icon | `h-4 w-4` on `ChevronLeft` / `ChevronRight` (lucide) | colour from `currentColor` → `--sf-pager-button-fg` |
| Page-position text | `px-2 text-sm text-gray-700` (also `aria-live="polite"`) | `--sf-pager-fg` |

### 3.1 Responsive padding

The root uses `px-4 py-3 sm:px-6` — at viewport widths ≥ Tailwind's `sm`
breakpoint (640px), horizontal padding bumps from `1rem` to `1.5rem`. This is
the only responsive breakpoint in M1. The token surface MAY introduce
`--sf-pager-padding-sm` / `--sf-pager-padding-md` if a future enhancement
needs more breakpoints; for M1, the single `--sf-pager-padding` token resolves
to a fixed-but-reasonable rhythm and the breakpoint shift is implementation
detail.

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | Pager rendered, no hover, no focus | all regions | base tokens |
| **Previous disabled** | `page === 1` (`canPrev === false`) | left chevron button | `disabled:opacity-40 disabled:cursor-not-allowed` → `--sf-pager-button-fg-disabled` |
| **Next disabled** | `page === pageCount` (`canNext === false`) | right chevron button | same recipe |
| **Both disabled** | `total === 0` or `pageCount === 1` | both chevron buttons | same recipe; summary text reads "No results" |
| **Hover on enabled chevron** | pointer over Previous or Next, not disabled | hovered button | `hover:bg-gray-100` → `--sf-pager-button-bg-hover` |
| **Focus-visible on chevron** | keyboard focus on Previous / Next | focused button | global `--sf-focus-ring-*` (browser default in M1; provider override) |
| **Focus on `<select>`** | keyboard focus or click on the page-size select | select control | `focus:border-blue-500 focus:outline-none` → `--sf-pager-select-focus-border` |
| **Page-size selector hidden** | `onPageSizeChange` is omitted | left cluster | renders only the summary text |
| **Empty data** | `total === 0` | summary region | "No results" replaces "Showing X–Y of Z"; chevrons are both disabled |

**State precedence (highest first):** disabled > focus > hover > default. The
`disabled:` Tailwind modifier sets opacity below the hover treatment so the
disabled button does NOT highlight on pointer over.

---

## 5. Composition with DataGrid

When Pager composes below DataGrid (the canonical pattern), the visual
expectation is:

- The DataGrid's bottom border (or last row's `border-b border-gray-100`) and
  the Pager's `border-t border-gray-200` together form a single seam (the
  Pager's top divider is the visual edge between the two regions).
- The horizontal padding alignment (`px-4` on both) keeps the chevron buttons
  visually-aligned with the table's left and right edges at small viewports;
  at `sm:` and up, both regions shift to `sm:px-6` (the table SHOULD adopt
  the same responsive padding for symmetry — currently DataGrid does not, see
  Open Questions §6).
- The font sizes match (`text-sm` in both Pager and DataGrid body cells) so
  the two regions read as one continuous data surface.

---

## 6. Open questions

1. **Tabular-numbers for "Showing X–Y of Z" and "Page N of M".** Both summary
   strings include numerals that change as the user pages. Without
   `font-variant-numeric: tabular-nums`, the text width jitters per digit
   width (e.g., "Showing 1–10 of 100" vs "Showing 91–100 of 100"). The M1
   default does NOT set tabular numerals. Should `--sf-pager-fg` carry a
   `font-variant-numeric` association, or should the recipe in §3 add
   `tabular-nums`? Recommend adding to the recipe; defer the token decision.
2. **Sticky positioning.** When Pager composes below a long DataGrid, hosts
   often want it sticky to the viewport bottom. Sticky behaviour is a
   layout concern (the consumer applies `position: sticky` on the parent
   wrapper); should Pager add a `sticky?: boolean` prop, or stay
   layout-neutral? Current contract: layout-neutral.
3. **`sm:` breakpoint asymmetry with DataGrid.** DataGrid's cells use a
   fixed `px-4`; Pager uses `px-4 sm:px-6`. The contract should align both
   surfaces. Recommend DataGrid adopt `sm:px-6` on its cells; track as a
   follow-on in DataGrid.Styling.
4. **`--sf-pager-summary-tabular` token.** If decision (1) goes the token
   route, the contract adds a numeric token that resolves to
   `font-variant-numeric: tabular-nums`. Out of scope for M1.

---

## 7. Do / Don't

### Do

- Consume `--sf-pager-*` via the recipes in §3; treat the Tailwind classes as
  the M1 default authoring layer.
- Disable both chevrons + render "No results" when `total === 0`.
- Render the page-size selector ONLY when `onPageSizeChange` is supplied —
  the visual asymmetry is the host's signal that the feature is opt-in.
- Honor `prefers-reduced-motion: reduce` on any hover/focus transition (M1
  default does not animate, so this is a forward-compat note).
- Align the Pager's horizontal padding with the composing DataGrid's cell
  padding so the seam reads as a single surface.

### Don't

- Don't put concrete hex values, RGB tuples, or other colour primitives in
  this contract or in `Pager.tsx`. Hex values belong only in
  `pagination.tokens.json`.
- Don't reuse `--sf-grid-*` tokens directly inside Pager's CSS. The grid and
  the pager are two separate components with two separate token surfaces; the
  alignment in §5 is visual, not token-level.
- Don't render the page-size selector as a generic Pager affordance when
  `onPageSizeChange` is omitted — the absence is the host's contract.
- Don't add an explicit `aria-live` to the chevron buttons; the summary text
  and page-position text already carry the live region (see Accessibility).
- Don't shrink the chevron button's hit region below the WCAG 2.2 SC 2.5.8
  minimum (24 × 24 CSS pixels). The M1 `p-1` + `h-4 w-4` icon yields
  effective 24 × 24; do not tighten further.

---

## 8. Parity notes

- **Blazor (future track):** the framework-neutral Pager (M0 backlog
  row 6) is a sibling component in the Navigation family. When a Blazor
  realisation lands, it consumes the same `--sf-pager-*` token surface.
- **React (this contract):** consumes via Tailwind utility classes; M1
  default.
- **Web Components (Phase M4, Lit):** TBD; consumes inside Shadow DOM with
  `::part(*)` exposure for hosts that want to re-style the chevron buttons
  or the selector.

---

## References

- ADR 0017 §A1.3 — DataGrid contract scope (pager seam reservation)
- [Pager.Semantic.md](./Pager.Semantic.md) — prop contract
- [Pager.Interaction.md](./Pager.Interaction.md) — behavioural contract
- [Pager.Accessibility.md](./Pager.Accessibility.md) — ARIA + keyboard
- [DataGrid.Styling.md](./DataGrid.Styling.md) — composing surface
- [DataGrid.Styling.md](./DataGrid.Styling.md) — pager-seam reservation
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- `_shared/design/tokens/pagination.tokens.json` — concrete default values (new file)
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
