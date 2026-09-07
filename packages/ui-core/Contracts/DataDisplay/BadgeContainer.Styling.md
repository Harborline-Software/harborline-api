# BadgeContainer — Styling Contract

- **Component:** BadgeContainer
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Draft
- **Companion contracts:** [Semantic](./BadgeContainer.Semantic.md) · [Interaction](./BadgeContainer.Interaction.md) · [Accessibility](./BadgeContainer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/BadgeContainer.tsx`

---

## 1. Purpose

`BadgeContainer` is a **structural positioning wrapper** — it establishes the
`position: relative` + `display: inline-flex` context an overlay `Badge`
anchors against, and paints nothing of its own (no background, border, text,
or radius). Its token surface is therefore intentionally minimal: it names
only the two layout primitives it fixes, and explicitly declares that it
carries **no** colour, spacing, typography, or radius tokens — those belong to
the wrapped element and to the overlay `Badge` (see [Badge.Styling](./Badge.Styling.md)).

This keeps the wrapper theme-transparent: changing a provider's badge palette
or the host's surface colours never re-themes the container, because the
container has nothing to re-theme.

---

## 2. Token surface

`BadgeContainer` exposes the `--sf-badge-container-*` family — two **structural**
tokens with fixed values. They are namespaced under `badge.container` in
`data-display.tokens.json` so they travel with the badge family.

| Token | Semantic role | Value | Varies | Notes |
|---|---|---|---|---|
| `--sf-badge-container-position` | Positioning context for the overlay `Badge` | `relative` | no | Anchors an absolutely-positioned `Badge` child to a corner of the wrapped element |
| `--sf-badge-container-display` | Box model of the wrapper | `inline-flex` | no | Shrink-wraps the wrapped element so the anchor box matches its content |

**No paint tokens.** `BadgeContainer` declares no `--sf-badge-container-bg`,
`-fg`, `-border`, `-radius`, `-padding`, or typography token. The wrapper is a
transparent box; adding any paint token here would be a contract violation
(the container must never compete visually with its content or the `Badge`).

---

## 3. Tailwind class recipe

```
relative inline-flex
```

The host's `className` is merged **after** the base recipe via `cn()`, so the
host sizes/spaces the wrapper (e.g. `className="h-10 w-10"`) without the
container imposing dimensions. The base recipe is the entire styling surface.

---

## 4. Visual state inventory

`BadgeContainer` is non-interactive and has **no visual states** — no hover,
focus, active, or disabled treatment of its own (per [Interaction §5](./BadgeContainer.Interaction.md)).

| State | Trigger | Recipe |
|---|---|---|
| **default** | always | `relative inline-flex` (single, invariant state) |
| hover / focus / active | — | NOT owned — belong to the wrapped control |
| disabled | — | NOT applicable — no `disabled` axis |

---

## 5. Responsive, RTL, reduced motion

- **Responsive:** the wrapper does not reflow or change by viewport; it
  shrink-wraps its content at every breakpoint.
- **RTL:** direction-agnostic — `inline-flex` + `relative` carry no directional
  padding or inset, so the container is correct under both `dir="ltr"` and
  `dir="rtl"`. The overlay `Badge`'s `position`/`align` owns corner placement
  and is responsible for its own logical-inset handling.
- **Reduced motion:** no animation; no `prefers-reduced-motion` handling
  required.

---

## 6. Token notes

- `BadgeContainer` consumes **no** semantic design tokens (it paints nothing).
- The visible overlay — count bubble, status dot — is the `Badge` child; its
  tokens are the `--sf-badge-*` family ([Badge.Styling §2](./Badge.Styling.md)).
- `NumberBadge` / `NotificationDot` build on this wrapper internally; they bring
  their own `Badge` paint, not container paint.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/badges/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
