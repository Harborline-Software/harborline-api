# EmptyState — Styling Contract

- **Component:** EmptyState
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Interaction](./EmptyState.Interaction.md) · [Accessibility](./EmptyState.Accessibility.md)
- **Related contract:** [DataGrid.Styling.md](./DataGrid.Styling.md) — DataGrid's `emptyState` slot accepts an EmptyState node; the two surfaces share a vertical-rhythm convention.
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** A18 EmptyState (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

EmptyState is a presentational placeholder for surfaces with no data: a
centred icon, a title, an optional secondary description, and an optional
single call-to-action button. This contract names the `--sf-empty-*` token
surface (icon colour per variant, typography hierarchy, spacing rhythm, CTA
treatment) and the Tailwind class recipes that act as the M1 default styling
layer.

The variant axis (`informational` | `positive` | `actionable`) is the only
contractual state: each variant tints the icon differently to carry the mood
signal. Title, description, and CTA do not vary by variant.

---

## 2. Token surface

EmptyState exposes the following CSS custom properties (the `--sf-empty-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Icon (varies by variant)

| Token | Semantic role | Varies by variant | Notes |
|---|---|---|---|
| `--sf-empty-icon-informational` | Icon colour for the informational variant | yes | Neutral / desaturated (e.g., `--sf-color-neutral-300`); communicates "passive empty" |
| `--sf-empty-icon-positive` | Icon colour for the positive variant | yes | Positive-success family (e.g., `--sf-color-success-300`); communicates "empty is good" |
| `--sf-empty-icon-actionable` | Icon colour for the actionable variant | yes | Same desaturated neutral as informational by design — the actionable variant signals "act" via the CTA button, NOT via icon-color emphasis. Keeping the icon neutral prevents two competing focal points. |
| `--sf-empty-icon-size` | Icon dimensions (height + width) | no | Typically `2rem` (= `h-8 w-8` in Tailwind); does not vary by variant |

The variant axis is mood-driven, but the *visual emphasis* in the actionable
case lives in the CTA button (§2.4), not the icon. This is a deliberate
design choice: actionable empty states demand user attention through an
action target, not through louder iconography.

### 2.2 Typography

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-empty-title-fg` | Foreground colour for the title text | none | Pairs with the host surface background at ≥ 4.5:1 |
| `--sf-empty-title-font-size` | Title font size | none | Typically `1rem` (= `text-base`) |
| `--sf-empty-title-font-weight` | Title font weight | none | Typically `500` (`font-medium`) |
| `--sf-empty-description-fg` | Foreground colour for the description text | none | Pairs with the host surface background at ≥ 4.5:1; intentionally lower-contrast than title to establish hierarchy |
| `--sf-empty-description-font-size` | Description font size | none | Typically `0.875rem` (= `text-sm`) |

### 2.3 Spacing

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-empty-padding-y` | Vertical padding of the centred column | none | Typically `3rem` (= `py-12`) |
| `--sf-empty-padding-x` | Horizontal padding | none | Typically `1.5rem` (= `px-6`) |
| `--sf-empty-gap-title` | Space between icon and title | none | Typically `0.75rem` (= `mt-3`) |
| `--sf-empty-gap-description` | Space between title and description | none | Typically `0.25rem` (= `mt-1`) |
| `--sf-empty-gap-action` | Space between description (or title, when no description) and CTA | none | Typically `1rem` (= `mt-4`) |

### 2.4 Call-to-action button

The CTA button reuses the application's standard button token surface where
one exists; M1 defaults are inline. The token surface here is the
EmptyState-local view of the CTA's appearance.

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-empty-action-bg` | CTA background fill | default | Typically `--sf-color-surface` (white); secondary-style button (outline, not solid) |
| `--sf-empty-action-bg-hover` | CTA background on hover | hover | Typically `--sf-color-neutral-50` |
| `--sf-empty-action-fg` | CTA foreground (label colour) | default | Pairs with `--sf-empty-action-bg` at ≥ 4.5:1 |
| `--sf-empty-action-border` | CTA border colour | default | Pairs with `--sf-empty-action-bg` at ≥ 3:1 |
| `--sf-empty-action-radius` | CTA corner radius | none | Typically `var(--sf-radius-md)` |
| `--sf-empty-action-padding` | CTA internal padding | none | Typically `0.5rem 1rem` (= `px-4 py-2`) |
| `--sf-empty-action-focus-ring` | CTA focus-visible ring colour | focus | Brand-accent (e.g., `--sf-color-primary`) |

### 2.5 Token additions to `data-display.tokens.json`

The `--sf-empty-*` family extends the existing `data-display.tokens.json`.
Recommended additions (derived from shipping Tailwind classes):

```jsonc
"emptyState": {
  "icon": {
    "_intent": "icon tint varies by variant; actionable shares neutral with informational to keep CTA as the focal point",
    "informational": "#D1D5DB",
    "positive": "#86EFAC",
    "actionable": "#D1D5DB",
    "size": "2rem"
  },
  "title": {
    "fg": "#374151",
    "fontSize": "1rem",
    "fontWeight": "500"
  },
  "description": {
    "fg": "#6B7280",
    "fontSize": "0.875rem"
  },
  "spacing": {
    "paddingY": "3rem",
    "paddingX": "1.5rem",
    "gapTitle": "0.75rem",
    "gapDescription": "0.25rem",
    "gapAction": "1rem"
  },
  "action": {
    "bg": "#FFFFFF",
    "bgHover": "#F9FAFB",
    "fg": "#374151",
    "border": "#D1D5DB",
    "radius": "6px",
    "padding": "0.5rem 1rem",
    "focusRing": "#3B82F6"
  },
  "_notes": {
    "contrast": "title.fg on host surface verified ≥ 4.5:1. description.fg on host surface verified ≥ 4.5:1 (lower contrast than title is deliberate; both must clear 4.5:1). action.fg on action.bg verified ≥ 4.5:1. action.border on action.bg verified ≥ 3:1. Icon colour is non-text; treated as decorative (see Accessibility §3).",
    "variant-actionable": "Per styling-contract design ruling, the actionable variant's icon colour matches informational; the visual emphasis lives in the CTA button, not the icon. Diverging the actionable icon to a brand-accent would create two competing focal points and is intentionally avoided."
  }
}
```

---

## 3. Tailwind class recipes (M1 default layer)

| Region | Tailwind recipe (M1 default) | Token resolution |
|---|---|---|
| Root container | `flex flex-col items-center py-12 px-6 text-center` | `--sf-empty-padding-y` + `--sf-empty-padding-x` |
| Icon (informational) | `h-8 w-8 text-gray-300` (`Info` lucide, `aria-hidden="true"`) | `--sf-empty-icon-size` + `--sf-empty-icon-informational` |
| Icon (positive) | `h-8 w-8 text-green-300` (`CheckCircle2`) | `--sf-empty-icon-size` + `--sf-empty-icon-positive` |
| Icon (actionable) | `h-8 w-8 text-gray-300` (`PlusCircle`) | `--sf-empty-icon-size` + `--sf-empty-icon-actionable` |
| Title | `mt-3 text-base font-medium text-gray-700` | `--sf-empty-gap-title` + `--sf-empty-title-*` |
| Description | `mt-1 text-sm text-gray-500` | `--sf-empty-gap-description` + `--sf-empty-description-*` |
| CTA button | `mt-4 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2` | `--sf-empty-gap-action` + full `--sf-empty-action-*` family |

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **variant: informational** | `variant === 'informational'` | icon | neutral grey (`text-gray-300`) |
| **variant: positive** | `variant === 'positive'` | icon | success green (`text-green-300`) |
| **variant: actionable** | `variant === 'actionable'` | icon | neutral grey (`text-gray-300`) — same as informational |
| **description present** | `description !== undefined` | layout | extra row inserted between title and CTA |
| **CTA present** | `action !== undefined` | layout | extra row inserted at the bottom |
| **CTA hover** | pointer over CTA | CTA | `hover:bg-gray-50` → `--sf-empty-action-bg-hover` |
| **CTA focus-visible** | keyboard focus on CTA | CTA | `focus:ring-2 focus:ring-blue-500 focus:ring-offset-2` → `--sf-empty-action-focus-ring` |
| **No icon** | (M1: not supported — icon is always rendered per variant) | n/a | n/a |

The variant axis is **mutually exclusive** — exactly one variant is active at
a time; tokens for the inactive variants do not consume runtime.

---

## 5. Composition inside DataGrid

When DataGrid passes an EmptyState node via its `emptyState` slot, the
EmptyState renders inside a `<td colSpan={effectiveColumns.length}>` per
DataGrid.Semantic §5. The visual expectation is:

- The EmptyState's centred column **horizontally centres within the table
  width**, not within the page width.
- The `py-12 px-6` padding gives the empty region perceivable visual presence
  inside the otherwise-rigid table layout — empty rows don't fade into the
  header/pager seams.
- The EmptyState does NOT render its own border or background; it inherits
  the table's row background (typically the same as `--sf-grid-row-bg-default`).

If a host wants a full-bleed marketing-style empty state (e.g., on a
top-level dashboard with no surrounding table), the host renders EmptyState
directly outside any table; the contract is the same.

---

## 6. Open questions

1. **Icon-color divergence for actionable.** The current design rules
   actionable icon = informational icon (neutral). If user research shows
   the actionable variant needs more visual pull (e.g., a brand-tinted
   `PlusCircle`), the token `--sf-empty-icon-actionable` can shift to a
   brand-primary tint independently of `--sf-empty-icon-informational`.
   The token surface accommodates the divergence today.
2. **Multi-CTA support.** The shipping prop shape is `action?: EmptyStateAction`
   (single CTA). Some patterns want a primary + secondary CTA pair
   ("Create" + "Learn more"). Out of scope for M1; revisit if usage data
   surfaces the need.
3. **Slotted icon.** Hosts may want to supply a custom icon (e.g., a
   product-specific glyph). The M1 implementation hard-codes lucide icons
   per variant. A future `icon?: ReactNode` slot prop is tracked in
   EmptyState.Semantic §7.
4. **Variant token unification.** The three variant icon tokens
   (`--sf-empty-icon-informational` / `-positive` / `-actionable`) could
   collapse to a single `--sf-empty-icon` with provider-side variant
   selectors. The split form is the M1 baseline because it makes the
   per-variant override surface explicit. Revisit if a provider needs
   wholesale icon-tint redesign.

---

## 7. Do / Don't

### Do

- Consume `--sf-empty-*` via the recipes in §3.
- Keep title and description contrasts above WCAG 2.2 SC 1.4.3 — both must
  clear 4.5:1, even though description is intentionally less prominent.
- Render the CTA as a native `<button type="button">` so platform keyboard
  activation, focus ring, and AT exposure are inherited.
- Centre the column horizontally inside its host surface (table cell or
  panel); the empty state is meant to feel like a self-contained "no data
  here" island.
- Honor `prefers-reduced-motion: reduce` for any future animation (M1 has
  none; this is forward-compat).

### Don't

- Don't put concrete hex values in this contract or in `EmptyState.tsx`.
  Hex values belong only in `data-display.tokens.json`.
- Don't elevate the actionable variant's icon-tint to a brand-accent unless
  the contract is amended — the CTA owns the focal point.
- Don't render a CTA when `action` is omitted; the component degrades to
  icon + title (+ optional description).
- Don't render the icon as a content-bearing element (e.g., with text
  inside). The icon is always decorative (see Accessibility §3) and must
  not carry semantic information.
- Don't tighten `py-12 px-6` significantly without re-verifying the
  centred-column reads as a visual island; cramped empty states feel like
  errors, not placeholders.

---

## 8. Parity notes

- **Blazor (future track):** consumes the same `--sf-empty-*` family. Icon
  glyphs come from `IHarborlineIconProvider` mapped by variant name.
- **React (this contract):** consumes via Tailwind utility classes; M1
  default with hard-coded lucide imports per variant.
- **Web Components (Phase M4, Lit):** TBD; consumes inside Shadow DOM with
  `::part(icon)`, `::part(title)`, `::part(description)`, `::part(action)`
  exposure for host overrides.

---

## References

- ADR 0017 §A1.3 — DataDisplay family contract scope
- [EmptyState.Semantic.md](./EmptyState.Semantic.md) — prop contract
- [EmptyState.Interaction.md](./EmptyState.Interaction.md) — behavioural contract
- [EmptyState.Accessibility.md](./EmptyState.Accessibility.md) — ARIA + keyboard
- [DataGrid.Styling.md](./DataGrid.Styling.md) — composing surface
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- `_shared/design/tokens/data-display.tokens.json` — concrete default values (additions for emptyState namespace)
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
