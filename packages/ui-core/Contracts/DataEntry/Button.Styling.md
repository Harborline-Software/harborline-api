# Button — Styling Contract

- **Component:** Button
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Button.Semantic.md) · [Interaction](./Button.Interaction.md) · [Accessibility](./Button.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/Button.tsx` (foundation: Radix Slot + shadcn Button)
- **Catalog row:** #17 Button (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

Button is the most foundational interactive primitive in the system: every
form, every dialog footer, every toolbar, every empty-state CTA composes on
it. This contract names the `--sf-btn-*` token surface for variant
(`primary` | `secondary` | `tertiary` | `destructive` | `ghost`), size
(`sm` | `md` | `lg`), and state (default / hover / active / focus-visible /
disabled / loading) — and pins the Tailwind class recipes that act as the
M2 default styling layer.

Button is shipping at M1 (wave-N extraction). The token surface is sized to
accommodate the variants and states catalogued under shadcn Button + Radix
Slot patterns. Where Radix/shadcn ship a sensible default, this contract
names the default; where the design system has a meaningful brand expression
opportunity, it names a token slot. The implementation today uses hard-coded
Tailwind utility classes (see §15.G-BST1) — the `--sf-btn-*` token surface
described here is the forward target for the variants-to-tokens migration.

---

## 2. Token surface

Button exposes the following CSS custom properties (the `--sf-btn-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Variant axis (background / foreground / border)

Five variants. Each variant declares a complete state-aware token set.

| Token | Semantic role | Varies by variant | Varies by state | Notes |
|---|---|---|---|---|
| `--sf-btn-bg-{variant}` | Background fill | yes | default / hover / active / disabled | Loading state inherits `default`; focus-visible does not change background |
| `--sf-btn-fg-{variant}` | Foreground (label + icon glyph) | yes | default / hover / active / disabled | Pairs with `--sf-btn-bg-{variant}` at ≥ 4.5:1 (text) per WCAG 2.2 SC 1.4.3 |
| `--sf-btn-border-{variant}` | Border colour | yes | default / hover / active / disabled | Pairs with `--sf-btn-bg-{variant}` at ≥ 3:1 per WCAG 2.2 SC 1.4.11 when border carries shape; `ghost` and `tertiary` MAY resolve border to `transparent` in default state |

The five variants are:

| `variant` | Intent | Background family | Use case |
|---|---|---|---|
| `primary` | Brand-accent primary action; one per logical surface | Brand-primary solid | "Save", "Submit", "Create" — the single most important action on a surface |
| `secondary` | Companion / alternate action | Neutral solid OR brand-tinted outline | "Cancel" paired with primary; "Edit" on a record |
| `tertiary` | Low-emphasis action, paired with content | Transparent + brand-tinted text | Inline actions in lists / cards where chrome is undesirable |
| `destructive` | Irreversible / data-loss action | Danger-red solid | "Delete", "Remove", "Discard" |
| `ghost` | Lowest emphasis; chromeless | Transparent | Icon-only toolbar buttons, in-row menu triggers |

**Cross-channel signal requirement.** `destructive` MUST NOT be carried by
colour alone — the label text MUST contain the destructive verb
("Delete" / "Remove" / "Discard"). See Accessibility §4 and WCAG 2.2 SC 1.4.1.

### 2.2 Size axis (padding / typography / icon)

Four sizes. The size axis is orthogonal to variant; every variant supports every size.

| Token | Semantic role | Varies by size | Notes |
|---|---|---|---|
| `--sf-btn-padding-y-{size}` | Vertical padding | yes | `sm` / `md` / `lg` |
| `--sf-btn-padding-x-{size}` | Horizontal padding | yes | `sm` / `md` / `lg` |
| `--sf-btn-font-size-{size}` | Label font size | yes | Typically `text-xs` / `text-sm` / `text-base` |
| `--sf-btn-line-height-{size}` | Label line height | yes | Matches font-size token so vertical alignment stays consistent |
| `--sf-btn-icon-size-{size}` | Embedded icon (Lucide / equivalent) dimensions | yes | Typically `0.875rem` / `1rem` / `1.25rem` |
| `--sf-btn-icon-gap-{size}` | Gap between icon and label | yes | Typically `0.375rem` / `0.5rem` / `0.5rem` |
| `--sf-btn-min-height-{size}` | Minimum tappable height | yes | MUST be ≥ `2.75rem` (44px) for `md` and `lg`; `sm` MAY drop to `2rem` (32px) only inside dense toolbars where Spacing Exception applies (WCAG 2.2 SC 2.5.8); `icon` matches `md` height (2.5rem) and forces `width = height` (square aspect); see Accessibility §5 |

### 2.3 State tokens (shared across variants)

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-btn-radius` | Corner radius | none | Typically `var(--sf-radius-md)`; same for all sizes/variants for visual consistency |
| `--sf-btn-focus-ring-color` | Focus-visible ring colour | focus only | Brand-accent (e.g., `--sf-color-primary`); MUST contrast against both the button background AND the surrounding surface at ≥ 3:1 per WCAG 2.2 SC 1.4.11 |
| `--sf-btn-focus-ring-width` | Focus-visible ring width | focus only | Typically `2px`; MUST be ≥ `2px` per WCAG 2.2 SC 2.4.13 Focus Appearance |
| `--sf-btn-focus-ring-offset` | Focus-visible ring offset | focus only | Typically `2px`; ensures ring is visually distinct from the button's own border |
| `--sf-btn-disabled-opacity` | Multiplier applied to the entire button when disabled | disabled only | Typically `0.5`; supplements the variant-specific `disabled` background/foreground tokens with a uniform fade so disabled buttons read consistently regardless of variant |
| `--sf-btn-loading-spinner-color` | Spinner glyph colour during loading state | loading only | Typically resolves to `--sf-btn-fg-{variant}` so the spinner inherits the label colour; MAY be overridden if the variant's `fg` lacks contrast against the variant's `bg` at spinner stroke widths |
| `--sf-btn-transition-duration` | Hover/active transition duration | all | Typically `150ms`; MUST be suppressed under `prefers-reduced-motion: reduce` (see §6) |

### 2.4 Token additions to `forms.tokens.json` (NEW)

The `--sf-btn-*` family lives in a new `forms.tokens.json` file under
`_shared/design/tokens/`. The file holds the button namespace plus form-
field primitives (FormField, TextField, etc. — already proposed in PAO M1).

Recommended initial structure for the button namespace:

```jsonc
"button": {
  "_intent": "five variants × three sizes × six states; cross-channel destructive signal required",
  "variant": {
    "primary": {
      "bgDefault": "#2563EB",
      "bgHover": "#1D4ED8",
      "bgActive": "#1E40AF",
      "bgDisabled": "#93C5FD",
      "fgDefault": "#FFFFFF",
      "fgHover": "#FFFFFF",
      "fgActive": "#FFFFFF",
      "fgDisabled": "#FFFFFF",
      "borderDefault": "transparent",
      "borderHover": "transparent",
      "borderActive": "transparent",
      "borderDisabled": "transparent"
    },
    "secondary": {
      "bgDefault": "#FFFFFF",
      "bgHover": "#F9FAFB",
      "bgActive": "#F3F4F6",
      "bgDisabled": "#FFFFFF",
      "fgDefault": "#111827",
      "fgHover": "#111827",
      "fgActive": "#111827",
      "fgDisabled": "#9CA3AF",
      "borderDefault": "#D1D5DB",
      "borderHover": "#9CA3AF",
      "borderActive": "#6B7280",
      "borderDisabled": "#E5E7EB"
    },
    "tertiary": {
      "bgDefault": "transparent",
      "bgHover": "#EFF6FF",
      "bgActive": "#DBEAFE",
      "bgDisabled": "transparent",
      "fgDefault": "#2563EB",
      "fgHover": "#1D4ED8",
      "fgActive": "#1E40AF",
      "fgDisabled": "#93C5FD",
      "borderDefault": "transparent",
      "borderHover": "transparent",
      "borderActive": "transparent",
      "borderDisabled": "transparent"
    },
    "destructive": {
      "bgDefault": "#DC2626",
      "bgHover": "#B91C1C",
      "bgActive": "#991B1B",
      "bgDisabled": "#FCA5A5",
      "fgDefault": "#FFFFFF",
      "fgHover": "#FFFFFF",
      "fgActive": "#FFFFFF",
      "fgDisabled": "#FFFFFF",
      "borderDefault": "transparent",
      "borderHover": "transparent",
      "borderActive": "transparent",
      "borderDisabled": "transparent"
    },
    "ghost": {
      "bgDefault": "transparent",
      "bgHover": "#F3F4F6",
      "bgActive": "#E5E7EB",
      "bgDisabled": "transparent",
      "fgDefault": "#374151",
      "fgHover": "#111827",
      "fgActive": "#111827",
      "fgDisabled": "#9CA3AF",
      "borderDefault": "transparent",
      "borderHover": "transparent",
      "borderActive": "transparent",
      "borderDisabled": "transparent"
    }
  },
  "size": {
    "sm":   { "paddingY": "0.25rem", "paddingX": "0.625rem", "fontSize": "0.75rem", "lineHeight": "1rem",   "iconSize": "0.875rem", "iconGap": "0.375rem", "minHeight": "2rem" },
    "md":   { "paddingY": "0.5rem",  "paddingX": "1rem",     "fontSize": "0.875rem","lineHeight": "1.25rem","iconSize": "1rem",     "iconGap": "0.5rem",   "minHeight": "2.5rem" },
    "lg":   { "paddingY": "0.625rem","paddingX": "1.25rem",  "fontSize": "1rem",    "lineHeight": "1.5rem", "iconSize": "1.25rem",  "iconGap": "0.5rem",   "minHeight": "2.75rem" },
    "icon": { "paddingY": "0",       "paddingX": "0",        "fontSize": "0",       "lineHeight": "0",      "iconSize": "1.25rem",  "iconGap": "0",        "minHeight": "2.5rem", "_note": "Square: width = minHeight. Icon IS the children; no label text. Adapter applies aspect-ratio:1 / equal h-N w-N classes." }
  },
  "shared": {
    "radius": "6px",
    "focusRingColor": "#2563EB",
    "focusRingWidth": "2px",
    "focusRingOffset": "2px",
    "disabledOpacity": 0.5,
    "transitionDuration": "150ms"
  }
}
```

Token-file PRs MAY ship alongside this contract or in a follow-up; the
contract is the source of truth either way.

---

## 3. Tailwind class recipes (M1 shipping layer)

The shipping React implementation uses Tailwind utility classes
(per fleet design-system convention). This section records the canonical
class recipes so adapter-equivalents (CSS Modules, styled-components,
vanilla CSS) can mirror them deterministically.

### 3.1 Base recipe (shared across all variants/sizes)

```
inline-flex items-center justify-center gap-{icon-gap} rounded-md font-medium
transition-colors disabled:cursor-not-allowed disabled:opacity-50
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2
focus-visible:ring-blue-600
```

### 3.2 Variant recipes

| Variant | Tailwind recipe (M2 default) |
|---|---|
| `primary` | `bg-blue-600 text-white hover:bg-blue-700 active:bg-blue-800 disabled:bg-blue-300` |
| `secondary` | `border border-gray-300 bg-white text-gray-900 hover:bg-gray-50 hover:border-gray-400 active:bg-gray-100 disabled:border-gray-200 disabled:text-gray-400` |
| `tertiary` | `text-blue-600 hover:bg-blue-50 hover:text-blue-700 active:bg-blue-100 active:text-blue-800 disabled:text-blue-300` |
| `destructive` | `bg-red-600 text-white hover:bg-red-700 active:bg-red-800 disabled:bg-red-300` |
| `ghost` | `text-gray-700 hover:bg-gray-100 hover:text-gray-900 active:bg-gray-200 disabled:text-gray-400` |

### 3.3 Size recipes

| Size | Tailwind recipe (padding + typography + min-height) |
|---|---|
| `sm` | `h-8 px-2.5 text-xs` |
| `md` | `h-10 px-4 text-sm` |
| `lg` | `h-11 px-5 text-base` |
| `icon` | `h-10 w-10 p-0` (square; no label; icon is `children`) |

### 3.4 Icon slot recipes

| Position | Recipe |
|---|---|
| Leading icon | `<Icon class="h-4 w-4 shrink-0" />` (sized per `--sf-btn-icon-size-{size}`) |
| Trailing icon | same as leading; positioned via parent flex-row order |
| Icon-only | add `aria-label` (Accessibility §3); square aspect via `aspect-square` + matching `h-N w-N` |
| Loading spinner | replaces leading icon during `loading` state; uses `animate-spin` + `motion-reduce:animate-none` (§6) |

### 3.5 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M2 default
authoring layer; the token surface is the contractual layer. A consumer who
wants to re-theme Button consumes the **tokens**, not the Tailwind classes.
A future provider package (FluentUI / Material / Bootstrap) MAY replace the
Tailwind recipe wholesale provided it preserves the token surface and the
state inventory in §4.

---

## 4. Visual state inventory

Button's state matrix (rows × columns = variant × state). Each cell resolves
to the variant/state token pair from §2.

| State | Trigger | Tokens affected | Recipe addendum |
|---|---|---|---|
| **default** | idle, enabled, no pointer/focus | `--sf-btn-bg-{variant}` (default), `--sf-btn-fg-{variant}` (default), `--sf-btn-border-{variant}` (default) | base recipe |
| **hover** | pointer enters; enabled | `--sf-btn-bg-{variant}` (hover), `--sf-btn-fg-{variant}` (hover), `--sf-btn-border-{variant}` (hover) | `hover:bg-{...}` |
| **active** | pointer down OR Enter/Space held; enabled | `--sf-btn-bg-{variant}` (active), `--sf-btn-fg-{variant}` (active) | `active:bg-{...}` |
| **focus-visible** | keyboard focus (NOT pointer focus) | adds focus ring per `--sf-btn-focus-ring-*` | `focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-blue-600` |
| **disabled** | `disabled` attribute OR `aria-disabled="true"` (focusable variant — see Accessibility §2) | `--sf-btn-bg-{variant}` (disabled), `--sf-btn-fg-{variant}` (disabled), uniform `--sf-btn-disabled-opacity` overlay | `disabled:opacity-50 disabled:cursor-not-allowed` |
| **loading** | `loading` prop true | inherits `default` background/foreground; leading icon slot becomes spinner | spinner replaces icon; label remains visible; button stays focusable (Accessibility §6) |

**State precedence (highest first):** disabled → loading → active → hover →
focus-visible (additive) → default.

`focus-visible` is **additive** — it layers a ring on top of whichever
state is active. The other states are mutually exclusive.

---

## 5. Responsive behaviour

Button does not reflow or change size based on viewport. The three sizes
(`sm` / `md` / `lg`) are author-chosen per usage context.

Button does **not** at M2:

- Collapse to icon-only on small viewports.
- Stack stacked button groups vertically on narrow widths.
- Swap variant by viewport (e.g., primary on desktop → tertiary on mobile).

These behaviours, if needed, belong to the containing layout component
(e.g., a `ButtonGroup` or form footer), not to Button itself.

---

## 6. Reduced motion

The hover/active transition (`transition-colors` with default `150ms`
duration) and the loading spinner animation (`animate-spin`) MUST honor
`prefers-reduced-motion: reduce`:

- Transition: shorten to `≤ 100ms` OR remove entirely. Tailwind's
  `motion-reduce:transition-none` is acceptable.
- Spinner: replace continuous rotation with a static glyph OR a slower
  pulse. Tailwind's `motion-reduce:animate-none` is acceptable.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 7. Open questions

1. **Outline-only secondary variant.** Some design systems ship `secondary`
   as a tinted-background button and `outline` as a separate variant. This
   contract treats `secondary` as outline-by-default to keep the variant
   count at five (matching shadcn). Confirm at council; if the system needs
   both a neutral-outline AND a brand-tinted-fill secondary, split into
   `outline` (current secondary) + `secondary` (new tinted-fill).
2. **Icon-only sizing.** Icon-only buttons SHOULD render as squares for
   touch-target consistency (e.g., `sm` icon-only = `h-8 w-8`, `md` = `h-10
   w-10`). Whether the size token should auto-derive square aspect, or
   whether `iconOnly: true` is a prop concern, is engineer-domain. The
   token surface supports either via `min-height` + `min-width` semantics.
3. **Loading state retains label vs. label hides.** The default recipe
   keeps the label visible alongside the spinner. Some systems hide the
   label and centre the spinner. This contract: label STAYS VISIBLE during
   loading to preserve width stability + label readability. Council may
   override for icon-only buttons (where there's no label to keep).
4. **Per-variant focus-ring colour.** The contract pins focus-ring colour
   to brand-primary across all variants. An alternate design pins
   focus-ring colour to the variant's own accent (e.g., `destructive`
   focus ring is red). The single-colour approach is more consistent;
   per-variant is more brand-flexible. Decision: single brand-primary
   ring; revisit if usability surfaces an issue.
5. **Compact density for dense toolbars.** `sm` size targets 32px min-
   height which falls under the WCAG 2.2 SC 2.5.8 Spacing Exception when
   adjacent buttons are at least 8px apart. Document the spacing rule in
   the Accessibility contract; do NOT introduce a fourth `xs` size unless
   council surfaces a use case.

---

## 8. Do / Don't

### Do

- Pair `--sf-btn-bg-{variant}` and `--sf-btn-fg-{variant}` such that every
  state's pair meets WCAG 2.2 SC 1.4.3 (≥ 4.5:1) for label text.
- Pair `--sf-btn-border-{variant}` against `--sf-btn-bg-{variant}` at ≥ 3:1
  per WCAG 2.2 SC 1.4.11 when the border carries shape.
- Reserve `primary` for the single most important action on a logical
  surface. Multiple primary buttons on one screen weaken every primary.
- Pair `destructive` variant with destructive verb in label text
  ("Delete" / "Remove" / "Discard").
- Provide `aria-label` on icon-only buttons (Accessibility §3).
- Use `focus-visible:` not `focus:` so pointer-focus does not show the ring.
- Honor `prefers-reduced-motion` on both the hover transition AND the
  loading spinner.

### Don't

- Don't ship more than one `primary` button per logical surface (form,
  dialog, toolbar section).
- Don't use colour alone to signal `destructive`. Pair with the verb.
- Don't put hex values in component code. Component CSS consumes
  `var(--sf-btn-*)` only.
- Don't introduce a new variant without adding it to this contract AND
  the catalog. Variants are part of the public API.
- Don't drop below 32px min-height on `sm` unless the Spacing Exception
  applies. Default to 40px (`md`) for stand-alone buttons.
- Don't hide the label during loading by default. Width stability matters.
- Don't use `transition-all`. Use `transition-colors` so transform/opacity
  changes (e.g., focus-ring scaling) are NOT animated.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD; pattern likely uses native `<button>`
  with class-based variant resolution.
- **React (this contract, M1 shipping):** shadcn Button + Radix Slot for
  `asChild` composition.
- **Web Components (Phase M4, Lit):** TBD; the WC track adopts whichever
  baseline is canonical at M4 entry. Tokens are framework-neutral and
  carry forward unchanged.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #17 Button (critical, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Button — Radix Slot + native button (reference implementation pattern)
- [Button.Semantic.md](./Button.Semantic.md) — prop contract
- [Button.Interaction.md](./Button.Interaction.md) — behavioural contract
- [Button.Accessibility.md](./Button.Accessibility.md) — ARIA + keyboard + focus
- `_shared/design/tokens/forms.tokens.json` (proposed; NEW file) — default token values
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.13 Focus Appearance
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BST1 | Medium | M1 implementation uses hard-coded Tailwind utility classes (`bg-blue-600`, `bg-red-600`, etc.) rather than `--sf-btn-*` CSS custom properties named in §2 | [ACCEPTED-RISK 2026-06-06] Token surface is the forward target. Tailwind-utility recipe in §3 is the M1 contract-of-record; tokens migration tracked as a follow-up amendment when `_shared/design/tokens/forms.tokens.json` lands |
| G-BST2 | Low | M1 implementation lacks dedicated `data-state` / `data-loading` attributes on the rendered element (relies on native `disabled` + className) | [ACCEPTED-RISK 2026-06-06] Visual-state inventory in §4 expresses state through className composition + disabled attr; data-state hooks are a follow-up if test-automation surfaces a need |
