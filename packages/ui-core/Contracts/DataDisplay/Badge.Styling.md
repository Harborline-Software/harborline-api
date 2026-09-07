# Badge — Styling Contract

- **Component:** Badge
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Badge.Semantic.md) · [Interaction](./Badge.Interaction.md) · [Accessibility](./Badge.Accessibility.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Badge_
- **Catalog row:** #9 Badge (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Badge is a small inline label communicating a count, a status, or a
category. It is presentational by default (non-interactive). This contract
names the `--sf-badge-*` token surface for variant
(`default` | `secondary` | `success` | `warning` | `danger` | `info`),
size (`sm` | `md`), and shape (`pill` | `square`) — and pins the Tailwind
class recipes that act as the M2 default styling layer.

Badge is closely related to StatusBanner (Feedback family) but operates at a
much smaller visual scale and conveys discrete category rather than a
prose message. Tokens are intentionally independent (badges shouldn't
re-theme when a provider changes the banner palette and vice versa).

Two orthogonal axes drive Badge's visual surface: **`variant`** (semantic
colour family: default / secondary / success / warning / danger / info) and
**`appearance`** (fill treatment: solid / subtle / outline). A third axis
**`shape`** controls border-radius (rounded / pill / square). These three
axes are fully independent.

---

## 2. Token surface

Badge exposes the following CSS custom properties (the `--sf-badge-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Variant axis (background / foreground / border)

Six variants. Each variant declares a complete (single-state) token set.

| Token | Semantic role | Varies by variant | Notes |
|---|---|---|---|
| `--sf-badge-bg-{variant}` | Background fill | yes | Tinted surface, NOT solid brand fill (badges should not compete with primary buttons) |
| `--sf-badge-fg-{variant}` | Foreground (label text + optional inline icon) | yes | Pairs with `--sf-badge-bg-{variant}` at ≥ 4.5:1 (text) per WCAG 2.2 SC 1.4.3 |
| `--sf-badge-border-{variant}` | Border colour | yes | Pairs with `--sf-badge-bg-{variant}` at ≥ 3:1 per WCAG 2.2 SC 1.4.11. MAY be `transparent` if the bg/fg pair alone carries sufficient hierarchy; recommended NON-transparent for outlined-on-light themes |

The six variants:

| `variant` | Intent | Background family | Use case |
|---|---|---|---|
| `default` | Neutral; counts, generic labels | Neutral-100 | "12 items", "Beta" |
| `secondary` | Slightly distinct neutral | Neutral-200 | Counterpart to default for visual variety |
| `success` | Positive state | Green-50 | "Active", "Paid", "Online" |
| `warning` | Caution state | Amber-50 | "Pending", "Overdue 3 days" |
| `danger` | Negative / error state | Red-50 | "Failed", "Offline", "Expired" |
| `info` | Informational state | Blue-50 | "New", "Draft", "Beta" |

**Cross-channel signal requirement.** State variants (`success` / `warning`
/ `danger` / `info`) MUST NOT carry their meaning by colour alone — the
badge label text MUST contain the semantic verb/noun ("Failed" not just
red dot). See Accessibility §4 and WCAG 2.2 SC 1.4.1.

### 2.2 Size axis (padding / typography / icon)

Two sizes. The size axis is orthogonal to variant.

| Token | Semantic role | Varies by size | Notes |
|---|---|---|---|
| `--sf-badge-padding-y-{size}` | Vertical padding | yes | Tight — badges are inline |
| `--sf-badge-padding-x-{size}` | Horizontal padding | yes | Tight |
| `--sf-badge-font-size-{size}` | Label font size | yes | `text-xs` / `text-sm` |
| `--sf-badge-line-height-{size}` | Label line height | yes | Set to font-size baseline so badges read as a single line |
| `--sf-badge-font-weight` | Label font weight | no (shared) | Typically `500` (`font-medium`) — slightly bolder than body text |
| `--sf-badge-icon-size-{size}` | Inline icon dimensions (when present) | yes | `0.75rem` / `0.875rem` |
| `--sf-badge-icon-gap-{size}` | Gap between icon and label | yes | `0.25rem` (`gap-1`) |

### 2.3 Appearance axis tokens

Three fill treatments. `appearance` is orthogonal to `variant` — every
variant supports every appearance.

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-badge-solid-bg-{variant}` | Full-saturation background for `solid` appearance | Pairs with `--sf-badge-solid-fg-{variant}` at ≥ 4.5:1. Uses the variant's saturated colour (e.g., `green-600` for success). |
| `--sf-badge-solid-fg-{variant}` | Foreground for `solid` appearance | Typically white for all variants |
| `--sf-badge-subtle-bg-{variant}` | Tinted background for `subtle` appearance (default) | Existing variant bg tokens. Low-saturation tint (`green-50` etc.). |
| `--sf-badge-subtle-fg-{variant}` | Foreground for `subtle` appearance | Existing variant fg tokens. Dark variant-hue text. |
| `--sf-badge-outline-fg-{variant}` | Foreground AND border colour for `outline` appearance | Matches variant accent colour. Pairs with transparent bg at ≥ 4.5:1 on host background. |

The existing variant token set (`--sf-badge-bg-{variant}` / `--sf-badge-fg-{variant}` / `--sf-badge-border-{variant}`) continues to serve the `subtle` appearance as its default. Adapters should alias them to `--sf-badge-subtle-*` or resolve them conditionally based on the `data-appearance` attribute.

| `appearance` | Background | Border | Text |
|---|---|---|---|
| `solid` | Saturated variant hue | None | White |
| `subtle` (default) | Tinted variant hue | Subtle tinted border | Dark variant-hue text |
| `outline` | Transparent | Variant accent | Variant accent |

### 2.4 Shape tokens

Three border-radius values. `shape` is orthogonal to `variant` and `appearance`.

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-badge-radius-rounded` | Default corner radius | Typically `0.375rem` (6px) — slight softening |
| `--sf-badge-radius-pill` | Corner radius for the pill shape | Typically `9999px` (`rounded-full`) — full pill |
| `--sf-badge-radius-square` | Corner radius for the square shape | `0` — no radius; sharp corners |

Shape is selected via prop (`shape="rounded" | "pill" | "square"`);
default is `rounded`.

### 2.5 Token additions to `data-display.tokens.json`

The `--sf-badge-*` family extends the existing `data-display.tokens.json`.
Recommended namespace addition:

```jsonc
"badge": {
  "_intent": "six variants × two sizes × two shapes; tinted-surface palette to avoid competing with primary buttons",
  "variant": {
    "default": {
      "bg": "#F3F4F6",
      "fg": "#1F2937",
      "border": "#E5E7EB"
    },
    "secondary": {
      "bg": "#E5E7EB",
      "fg": "#111827",
      "border": "#D1D5DB"
    },
    "success": {
      "bg": "#ECFDF5",
      "fg": "#065F46",
      "border": "#A7F3D0"
    },
    "warning": {
      "bg": "#FFFBEB",
      "fg": "#92400E",
      "border": "#FDE68A"
    },
    "danger": {
      "bg": "#FEF2F2",
      "fg": "#991B1B",
      "border": "#FECACA"
    },
    "info": {
      "bg": "#EFF6FF",
      "fg": "#1E40AF",
      "border": "#BFDBFE"
    }
  },
  "size": {
    "sm": { "paddingY": "0.125rem", "paddingX": "0.5rem",  "fontSize": "0.625rem", "lineHeight": "0.75rem",  "iconSize": "0.75rem",   "iconGap": "0.25rem" },
    "md": { "paddingY": "0.25rem",  "paddingX": "0.625rem","fontSize": "0.75rem",  "lineHeight": "1rem",     "iconSize": "0.875rem",  "iconGap": "0.25rem" }
  },
  "appearance": {
    "_note": "Orthogonal to variant. subtle = default (re-uses existing variant bg/fg tokens). solid/outline require variant-specific overrides.",
    "solid":   { "_note": "Saturated bg + white fg per variant. Exact values defined per-variant by provider themes." },
    "subtle":  { "_note": "Tinted bg + dark variant fg. This is the M2 default — aliases to existing variant bg/fg/border tokens." },
    "outline": { "_note": "Transparent bg + variant-accent border + variant-accent text. Provider themes supply per-variant accent colours." }
  },
  "shape": {
    "rounded": "0.375rem",
    "pill": "9999px",
    "square": "0"
  },
  "fontWeight": 500,
  "_notes": {
    "contrast": "Every variant's bg/fg pair is verified ≥ 4.5:1 per WCAG 2.2 SC 1.4.3. Every variant's bg/border pair is verified ≥ 3:1 per WCAG 2.2 SC 1.4.11. Adapters MUST re-verify if a provider override changes these defaults.",
    "interactiveBadge": "Badge is presentational by default. If the consumer composes Badge inside an interactive surface (e.g., a clickable tag), the interactive element above Badge owns focus/hover/active; Badge tokens do NOT include hover/focus states.",
    "darkMode": "Dark-theme overrides are NOT in this file. Provider theme packages supply dark-mode token sets and toggle via [data-theme='dark'] or equivalent."
  }
}
```

---

## 3. Tailwind class recipes (M2 default layer)

### 3.1 Base recipe (shared across all variants/sizes)

```
inline-flex items-center gap-1 font-medium whitespace-nowrap
```

### 3.2 Variant recipes

| Variant | Tailwind recipe |
|---|---|
| `default` | `bg-gray-100 text-gray-800 border border-gray-200` |
| `secondary` | `bg-gray-200 text-gray-900 border border-gray-300` |
| `success` | `bg-green-50 text-green-800 border border-green-200` |
| `warning` | `bg-amber-50 text-amber-800 border border-amber-200` |
| `danger` | `bg-red-50 text-red-800 border border-red-200` |
| `info` | `bg-blue-50 text-blue-800 border border-blue-200` |

### 3.3 Size recipes

| Size | Tailwind recipe |
|---|---|
| `sm` | `px-2 py-0.5 text-[10px] leading-3` |
| `md` | `px-2.5 py-1 text-xs leading-4` |

### 3.4 Shape recipes

| Shape | Tailwind recipe |
|---|---|
| `rounded` (default) | `rounded-md` |
| `pill` | `rounded-full` |
| `square` | `rounded-none` |

### 3.5 Appearance recipes

| Appearance | Tailwind recipe |
|---|---|
| `subtle` (default) | inherits base recipe (tinted bg + border + dark text — already the default variant classes) |
| `solid` | `bg-{variant-saturated} text-white border-transparent` (e.g., `bg-green-600 text-white` for success) |
| `outline` | `bg-transparent text-{variant-accent} border border-{variant-accent}` (e.g., `bg-transparent text-green-700 border-green-700` for success) |

### 3.6 Icon slot

```
<Icon class="h-3 w-3 shrink-0" />  // sm
<Icon class="h-3.5 w-3.5 shrink-0" />  // md
```

Icons are decorative by default (`aria-hidden="true"`) — the badge's
accessible name comes from its text content.

---

## 4. Visual state inventory

Badge is non-interactive in its default usage. The state matrix is therefore
trivial: every variant has a single visual state.

| State | Trigger | Notes |
|---|---|---|
| **default** | always | Single visual state per variant |
| **hover/focus/active** | — | NOT supported at the Badge level. Composing Badge inside a `<button>` or `<a>` puts those states on the parent, not on Badge |
| **disabled** | — | NOT applicable; Badge has no enabled/disabled axis |

If a consumer needs an interactive "chip" with badge styling + hover/focus,
that is a separate component (Chip — out of scope at M2). Badge stays
strictly presentational.

---

## 5. Responsive behaviour

Badge does not reflow or change size based on viewport. The two sizes
(`sm` / `md`) are author-chosen per usage context.

Badge does **not** at M2:

- Stack vertically on narrow viewports.
- Truncate label text with ellipsis. Badges are short — if the label is
  too long, the badge is being misused.
- Switch shape by viewport.

---

## 6. Reduced motion

Badge has NO animations by default. No reduced-motion handling required.

If a future enhancement adds a "new" pulse or appear animation, that MUST
honor `prefers-reduced-motion: reduce` per WCAG 2.2 SC 2.3.3.

---

## 7. Open questions

1. **Tinted-surface vs. solid-fill palette.** This contract uses tinted-
   surface variants (e.g., `bg-green-50 text-green-800`) rather than solid
   brand fills. Some design systems prefer solid (e.g., GitHub's `success`
   badge is solid green-600 + white text). Decision: tinted-surface, because
   the typical use case (status indicator beside content) does not warrant
   the visual weight of solid colour. Council may override per brand
   direction; the contract carries one default.
2. **Removable-badge variant.** Some systems ship a "removable" badge with
   an `×` dismiss button. That would be an interactive variant (Chip-like)
   and is out of M2 scope. If added, it becomes a separate Chip component.
3. **Dot-only badge.** A 6×6 coloured dot (no label) is sometimes used as
   a notification indicator. That is NOT Badge — it's a distinct
   StatusIndicator primitive (not in M2 catalog). The Badge contract does
   NOT support label-less rendering.
4. **Maximum content length.** Badges should hold short text (typically ≤ 12
   chars). The contract does NOT enforce a max; design discipline is the
   only constraint. Council may add a dev-mode warning when label > N chars.
5. **Brand-primary variant.** The six variants do not include a "primary"
   (brand-blue) badge — `info` covers the brand-blue use case at the badge
   scale. If a system needs both, add `primary` as a seventh variant in a
   follow-on amendment.

---

## 8. Do / Don't

### Do

- Pair `--sf-badge-bg-{variant}` and `--sf-badge-fg-{variant}` such that
  every pair meets WCAG 2.2 SC 1.4.3 (≥ 4.5:1) for the label.
- Pair `--sf-badge-border-{variant}` against `--sf-badge-bg-{variant}` at
  ≥ 3:1 per WCAG 2.2 SC 1.4.11.
- Pair state variants (`success` / `warning` / `danger` / `info`) with a
  semantic label ("Active" / "Pending" / "Failed" / "New") — not colour
  alone.
- Keep badge content short — typically a single word or a number.
- Treat inline icons as decorative (`aria-hidden="true"`).

### Don't

- Don't ship a Badge with hover/focus/active styles — those belong to the
  interactive parent if Badge is composed inside one.
- Don't use colour alone for state. The label carries the meaning.
- Don't put hex values in component code. Component CSS consumes
  `var(--sf-badge-*)` only.
- Don't introduce a "destructive action" badge — that's a Button concern,
  not Badge.
- Don't use Badge as a notification dot. Dot indicators are a separate
  primitive.
- Don't truncate badge content with ellipsis — shorten the source string.

---

## 9. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Badge — `<span>` with
  variant + size CVA classes.
- **Web Components (Phase M4, Lit):** TBD; the WC track adopts whichever
  baseline is canonical at M4 entry. Tokens are framework-neutral.

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #9 Badge (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Badge — reference foundation
- [Badge.Semantic.md](./Badge.Semantic.md) — prop contract
- [Badge.Interaction.md](./Badge.Interaction.md) — behavioural contract
- [Badge.Accessibility.md](./Badge.Accessibility.md) — ARIA + colour-independence
- `_shared/design/tokens/data-display.tokens.json` — default token values
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
