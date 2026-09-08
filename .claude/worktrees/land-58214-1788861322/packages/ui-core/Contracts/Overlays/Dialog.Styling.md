# Dialog — Styling Contract

- **Component:** Dialog
- **ADR 0017 family:** Overlays
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Dialog.Semantic.md) · [Interaction](./Dialog.Interaction.md) · [Accessibility](./Dialog.Accessibility.md)
- **Related contract:** [ConfirmDialog.Styling.md](./ConfirmDialog.Styling.md) — ConfirmDialog composes Dialog and supplies its own footer buttons; this contract defines the parent's shared surface.
- **Reference implementation:** `packages/ui-react/src/components/dialogs/Dialog.tsx` (built on `@radix-ui/react-dialog`)
- **Catalog row:** #43 Dialog (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

Dialog is the canonical modal-overlay surface of `@harborline-software/ui-react`, built
on `@radix-ui/react-dialog`. It has four visible regions:

1. **Backdrop** — a fixed full-viewport tinted layer beneath the dialog
   content, dimming the page chrome.
2. **Content panel** — the floating card centred in the viewport,
   containing title / description / body / optional footer.
3. **Close affordance** — the `×` icon button in the top-right of the
   header.
4. **Footer** — an optional bottom-aligned row for action buttons
   (typically rendered by ConfirmDialog or by hosts).

This contract names the `--sf-dialog-*` token surface, the entry / exit
animation pattern (Radix's `data-[state=open|closed]` data attributes
driving Tailwind's animate-in / animate-out utilities), and the visual
state inventory.

---

## 2. Token surface

Dialog exposes the following CSS custom properties (the `--sf-dialog-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Backdrop

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-overlay-bg` | Backdrop colour (with alpha) | open | Typically `rgba(0, 0, 0, 0.4)` — neutral dim, not branded |
| `--sf-dialog-overlay-z-index` | Backdrop stacking order | none | Numeric; M1 baseline `50` |

### 2.2 Content panel

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-content-bg` | Panel background fill | none | Typically `--sf-color-surface` (white) |
| `--sf-dialog-content-radius` | Panel corner radius | none | Typically `var(--sf-radius-lg)` — larger than form radius to emphasise the modal weight |
| `--sf-dialog-content-shadow` | Panel drop-shadow / elevation | none | Heavy elevation (e.g., `var(--sf-elevation-4)`); the panel must visually float above the dimmed backdrop |
| `--sf-dialog-content-max-width` | Panel maximum width | none | Typically `32rem` (= `max-w-lg`); content wraps wider hosts |
| `--sf-dialog-content-z-index` | Panel stacking order | none | Numeric; M1 baseline `50` (same as backdrop; Radix orders them via DOM order inside the portal) |

### 2.3 Header

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-header-padding` | Header internal padding | none | Typically `1rem 1.5rem` (= `px-6 py-4`) |
| `--sf-dialog-header-border-bottom` | Bottom divider between header and body | none | Pairs with `--sf-dialog-content-bg` at ≥ 3:1 |
| `--sf-dialog-title-fg` | Title text colour | none | Pairs with `--sf-dialog-content-bg` at ≥ 4.5:1 |
| `--sf-dialog-title-font-size` | Title font size | none | Typically `1rem` (= `text-base`) |
| `--sf-dialog-title-font-weight` | Title font weight | none | Typically `600` (= `font-semibold`) |
| `--sf-dialog-description-fg` | Description text colour | none | Pairs at ≥ 4.5:1; lower-contrast than title for hierarchy |
| `--sf-dialog-description-font-size` | Description font size | none | Typically `0.875rem` (= `text-sm`) |
| `--sf-dialog-description-gap-top` | Vertical gap between title and description | none | Typically `0.25rem` (= `mt-1`) |

### 2.4 Close button

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-close-fg` | Close-icon colour (default) | default | Pairs at ≥ 3:1 against header background |
| `--sf-dialog-close-fg-hover` | Close-icon colour on hover | hover | Darker shade for hover affordance |
| `--sf-dialog-close-bg-hover` | Close-button background on hover | hover | Tinted neutral overlay (e.g., `--sf-color-neutral-100`) |
| `--sf-dialog-close-ring-focus` | Focus-visible ring | focus | Brand-accent; pairs at ≥ 3:1 |
| `--sf-dialog-close-radius` | Close-button corner radius | none | Typically `var(--sf-radius-sm)` |
| `--sf-dialog-close-padding` | Close-button hit-area padding | none | Typically `0.25rem` (= `p-1`) → with the `h-4 w-4` icon yields effective 24 × 24 target |
| `--sf-dialog-close-margin-left` | Gap between title block and close button | none | Typically `1rem` (= `ml-4`) |

### 2.5 Body

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-body-padding` | Body internal padding | none | Typically `1rem 1.5rem` (= `px-6 py-4`) |

### 2.6 Footer

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-footer-padding` | Footer internal padding | none | Typically `1rem 1.5rem` (= `px-6 py-4`) — matches header for symmetry |
| `--sf-dialog-footer-border-top` | Top divider between body and footer | none | Pairs at ≥ 3:1; same colour as header bottom-border |
| `--sf-dialog-footer-gap` | Horizontal gap between footer buttons | none | Typically `0.75rem` (= `gap-3`) |

### 2.7 Animation

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-dialog-animation-duration` | Open/close transition duration | open / closed | Typically `~150ms` (Tailwind's `animate-in` default); reduced-motion users get `0ms` |
| `--sf-dialog-animation-fade` | Opacity transition (backdrop + panel) | open / closed | `0 → 1` on open, `1 → 0` on close |
| `--sf-dialog-animation-zoom` | Scale transition (panel only) | open / closed | `0.95 → 1` on open, `1 → 0.95` on close |
| `--sf-dialog-animation-slide-y` | Vertical slide (panel only) | open / closed | Subtle ~48% vertical offset; sense of "rising into place" |

### 2.8 Token additions — new file `overlays.tokens.json`

Recommended new file `_shared/design/tokens/overlays.tokens.json`:

```jsonc
{
  "$schema": "https://design-tokens.github.io/community-group/format/",
  "_meta": {
    "name": "overlays.tokens",
    "description": "Default design-system values for the Overlays component family. Shared by Dialog, ConfirmDialog, and future Drawer / Sheet / Popover overlays.",
    "adr": "0017-A1",
    "status": "Draft",
    "version": "0.1.0"
  },
  "dialog": {
    "overlay": { "bg": "rgba(0, 0, 0, 0.4)", "zIndex": 50 },
    "content": {
      "bg": "#FFFFFF",
      "radius": "0.5rem",
      "shadow": "0 25px 50px -12px rgba(0,0,0,0.25)",
      "maxWidth": "32rem",
      "zIndex": 50
    },
    "header": {
      "padding": "1rem 1.5rem",
      "borderBottom": "#E5E7EB"
    },
    "title": { "fg": "#111827", "fontSize": "1rem", "fontWeight": "600" },
    "description": { "fg": "#6B7280", "fontSize": "0.875rem", "gapTop": "0.25rem" },
    "close": {
      "fg": "#9CA3AF",
      "fgHover": "#4B5563",
      "bgHover": "#F3F4F6",
      "ringFocus": "#3B82F6",
      "radius": "0.25rem",
      "padding": "0.25rem",
      "marginLeft": "1rem"
    },
    "body": { "padding": "1rem 1.5rem" },
    "footer": { "padding": "1rem 1.5rem", "borderTop": "#E5E7EB", "gap": "0.75rem" },
    "animation": {
      "duration": "150ms",
      "fadeFrom": 0, "fadeTo": 1,
      "zoomFrom": 0.95, "zoomTo": 1,
      "slideY": "48%"
    },
    "_notes": {
      "contrast": "title.fg, description.fg verified ≥ 4.5:1 on content.bg. close.fg on content.bg verified ≥ 3:1 (non-text icon). header.borderBottom / footer.borderTop on content.bg verified ≥ 3:1.",
      "reduced-motion": "When `prefers-reduced-motion: reduce`, ALL animation is disabled — instant pop-in. This means `motion-reduce:animate-none` removes both the backdrop fade AND the content zoom/slide. Do not preserve a 'subtle' fade for reduced-motion users; the preference means no animation. WCAG 2.2 SC 2.3.3 requires the animation to be suppressible, which instant-pop-in satisfies completely.",
      "stacking": "overlay.zIndex and content.zIndex both = 50 because Radix renders them inside the same portal in DOM order — overlay first (behind), content second (above). Hosts that need to stack a popover or toast above an open dialog must use zIndex > 50."
    }
  }
}
```

---

## 3. Tailwind class recipes (M1 default layer)

### 3.1 Backdrop (overlay)

```
fixed inset-0 z-50 bg-black/40
data-[state=open]:animate-in data-[state=closed]:animate-out
data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0
motion-reduce:animate-none
```

`bg-black/40` is the visual realisation of `--sf-dialog-overlay-bg`
(opacity 40% black).

### 3.2 Content panel

```
fixed left-1/2 top-1/2 z-50 w-full max-w-lg -translate-x-1/2 -translate-y-1/2 rounded-lg bg-white shadow-xl

data-[state=open]:animate-in data-[state=closed]:animate-out
data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0
data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95
data-[state=closed]:slide-out-to-left-1/2 data-[state=closed]:slide-out-to-top-[48%]
data-[state=open]:slide-in-from-left-1/2 data-[state=open]:slide-in-from-top-[48%]
motion-reduce:animate-none
```

The translate values (-50% on both axes) plus left/top 50% centre the panel
in the viewport. The animation recipe combines fade + zoom + subtle slide
for the "rises into place" feel.

### 3.3 Header

```
flex items-start justify-between border-b border-gray-200 px-6 py-4

(title)        text-base font-semibold text-gray-900
(description)  mt-1 text-sm text-gray-500
```

The title/description block goes in a single `<div>`; the close button is
the second child of the header flex (justify-between pushes them apart).

### 3.4 Close button

```
ml-4 rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600
focus:outline-none focus:ring-1 focus:ring-blue-500
```

Contains `<X className="h-4 w-4" aria-hidden="true" />`.

### 3.5 Body

```
px-6 py-4
```

(content is host-supplied via `children` prop)

### 3.6 Footer (when supplied)

```
flex items-center justify-end gap-3 border-t border-gray-200 px-6 py-4
```

Right-aligned action row.

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **closed** | `open === false` | dialog tree not rendered (Radix removes from DOM) | n/a |
| **opening** | Radix `data-state="open"` transition | backdrop + panel | `animate-in fade-in-0 zoom-in-95 slide-in-from-*` |
| **open** | Radix `data-state="open"` (steady) | backdrop + panel | base tokens, no animation |
| **closing** | Radix `data-state="closed"` transition | backdrop + panel | `animate-out fade-out-0 zoom-out-95 slide-out-to-*` |
| **close-button hover** | pointer over close `×` | close button | `hover:bg-gray-100 hover:text-gray-600` |
| **close-button focus-visible** | keyboard focus on close `×` | close button | `focus:ring-1 focus:ring-blue-500` |
| **footer absent** | `footer` prop is undefined | footer region | not rendered; no border-top |
| **description absent** | `description` prop is undefined | description region | not rendered; title only |
| **reduced motion** | `prefers-reduced-motion: reduce` | animations | disabled (instant pop-in); `motion-reduce:animate-none` on all animated elements — see §2.7 |

**State precedence (animation):** opening / closing transitions are
mutually exclusive (Radix orchestrates the sequence).

---

## 5. Composition with ConfirmDialog and host content

Dialog is the parent surface; ConfirmDialog composes Dialog and supplies
its own footer buttons via the `footer` prop. Hosts may also build custom
dialogs (e.g., an edit form):

```tsx
<Dialog open={isOpen} onOpenChange={setOpen} title="Edit property" description="Update the address fields.">
  <form>...</form>
  {/* No footer here — the form's submit button is inside the body */}
</Dialog>
```

OR with an explicit footer:

```tsx
<Dialog
  open={isOpen} onOpenChange={setOpen}
  title="Edit property"
  footer={
    <>
      <button onClick={cancel}>Cancel</button>
      <button onClick={save}>Save</button>
    </>
  }
>
  <form>...</form>
</Dialog>
```

Both patterns work. The footer slot is host-styled — Dialog provides only
the row-layout (`flex items-center justify-end gap-3`) and the border-top
seam.

---

## 6. Open questions

1. **Side variants (left / right / bottom panels).** Dialog is centred-
   modal only in M1. A future Drawer / Sheet component may live alongside,
   sharing some tokens (overlay, animation duration). Out of scope for
   M1 Dialog contract.
2. **`max-width` exposure as a prop.** The M1 baseline is `max-w-lg`
   (32rem). Hosts who need a wider modal (e.g., a complex form) currently
   must override via custom CSS. A `size?: 'sm' | 'md' | 'lg' | 'xl' | 'full'`
   prop is a likely future enhancement; out of scope for M1.
3. **Inline-validation footer support.** Some dialogs want a left-aligned
   error / warning message in the footer (alongside the right-aligned
   action buttons). Out of scope for M1.
4. **Reduced-motion strategy.** The canonical implementation uses the Tailwind
   `motion-reduce:animate-none` class on both the backdrop and content panel
   (see §3.1, §3.2). This suppresses all `animate-in` / `animate-out`
   transitions when the user has `prefers-reduced-motion: reduce` set. **Do NOT**
   implement reduced-motion via a raw `@media (prefers-reduced-motion: reduce)`
   CSS rule — both approaches are valid CSS but mixing them in the same
   component tree creates unpredictable interaction with Radix's state machine
   which controls `data-[state=*]` attributes. The Tailwind variant is the
   single canonical approach for this component.

---

## 7. Do / Don't

### Do

- Consume `--sf-dialog-*` via the recipes in §3.
- Render the backdrop AND the content panel at `z-50` so they stack above
  page chrome; let Radix's portal handle the DOM ordering.
- Keep the close button at `p-1` + `h-4 w-4` icon for the 24 × 24
  effective target.
- Use Radix's `data-[state=*]:animate-*` recipe for entrance/exit
  animation — it integrates with Radix's lifecycle and honors reduced-
  motion automatically when the `motion-reduce:` variant is added.
- Match header padding (`px-6 py-4`) to footer padding for visual rhythm
  symmetry.
- Use heavy shadow (`shadow-xl`) on the content panel so it visibly floats
  above the dimmed backdrop.

### Don't

- Don't put concrete hex values in this contract or in `Dialog.tsx`.
  Hex values belong only in `overlays.tokens.json`.
- Don't render the dialog inline in the host's DOM — always use Radix's
  portal (`<DialogPrimitive.Portal>`) so the modal escapes any
  `overflow: hidden` or `transform` ancestor.
- Don't omit the close affordance unless the dialog is unequivocally
  forced (e.g., a license-acceptance modal). Even then, prefer to leave
  the close present and validate intent on close.
- Don't use a saturated colour for the backdrop. Neutral dim
  (`rgba(0,0,0,0.4)`) is the safe default.
- Don't add `aria-modal` via the styling layer — the accessibility
  contract owns that emission.

---

## 8. Parity notes

- **Blazor (future HarborlineDialog track):** consumes the same
  `--sf-dialog-*` family. Same Radix-equivalent portal + focus-trap
  pattern.
- **React (this contract):** as documented, with Radix UI primitives.
- **Web Components (Phase M4, Lit):** TBD; consumes inside Shadow DOM
  with `::part(overlay)`, `::part(content)`, `::part(close)` exposure.
  Web Components have built-in portal-equivalents via
  `popover` / `popoverapi`; whether to use those vs a manual implementation
  is a Phase M4 decision.

---

## References

- ADR 0017 §A1.3 — Overlays family contract scope
- [Dialog.Semantic.md](./Dialog.Semantic.md) — prop contract
- [Dialog.Interaction.md](./Dialog.Interaction.md) — behavioural contract
- [Dialog.Accessibility.md](./Dialog.Accessibility.md) — ARIA + focus trap
- [ConfirmDialog.Styling.md](./ConfirmDialog.Styling.md) — composing surface
- Radix UI — `@radix-ui/react-dialog`
- `_shared/design/tokens/overlays.tokens.json` — concrete default values (new file)
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
