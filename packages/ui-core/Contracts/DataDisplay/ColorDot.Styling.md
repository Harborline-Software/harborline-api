# ColorDot — Styling Contract

- **Component:** ColorDot
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorDot.Semantic.md) · [Interaction](./ColorDot.Interaction.md) · [Accessibility](./ColorDot.Accessibility.md) · [Styling](./ColorDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/ColorDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ColorDot renders a small filled circle in one of 14 named hues at one of 4 sizes. This contract documents the Tailwind class recipes used by the reference implementation and identifies the token migration path.

---

## 2. Tailwind class recipes

### 2.1 Base (outer span)

```
relative inline-flex
```

### 2.2 Colour recipes

| `color` | Dot class | Pulse ring class (same) |
|---|---|---|
| `gray` | `bg-gray-400` | `bg-gray-400` |
| `red` | `bg-red-500` | `bg-red-500` |
| `orange` | `bg-orange-500` | `bg-orange-500` |
| `amber` | `bg-amber-500` | `bg-amber-500` |
| `yellow` | `bg-yellow-400` | `bg-yellow-400` |
| `lime` | `bg-lime-500` | `bg-lime-500` |
| `green` | `bg-green-500` | `bg-green-500` |
| `teal` | `bg-teal-500` | `bg-teal-500` |
| `cyan` | `bg-cyan-500` | `bg-cyan-500` |
| `blue` | `bg-blue-500` | `bg-blue-500` |
| `indigo` | `bg-indigo-500` | `bg-indigo-500` |
| `violet` | `bg-violet-500` | `bg-violet-500` |
| `purple` | `bg-purple-500` | `bg-purple-500` |
| `pink` | `bg-pink-500` | `bg-pink-500` |

### 2.3 Size recipes (inner dot span)

| `size` | Tailwind classes |
|---|---|
| `xs` | `h-1.5 w-1.5` |
| `sm` | `h-2 w-2` |
| `md` | `h-2.5 w-2.5` |
| `lg` | `h-3 w-3` |

Inner dot additional classes: `relative inline-flex rounded-full border border-foreground
forced-colors:border-[CanvasText] forced-colors:bg-[CanvasText]`. The boundary gives every hue a
theme-aware 3:1 non-text contrast edge. In forced-colors mode, `CanvasText` keeps the dot visible
against the system canvas.

### 2.4 Pulse ring recipe

When `pulse={true}`:

```
absolute inline-flex h-full w-full animate-ping rounded-full opacity-75
```

The pulse ring gets the same colour class as the dot. It is `aria-hidden`.

**Reduced motion note:** `animate-ping` should use `motion-safe:animate-ping` to honour `prefers-reduced-motion`. This is a known gap (see Accessibility §9.A1).

---

## 3. Token surface

ColorDot does not currently use `--sf-*` CSS custom properties. Colours are hard-coded Tailwind classes. Token migration (if desired):

| Proposed token | Tailwind fallback | Notes |
|---|---|---|
| `--sf-dot-color-{hue}` | `bg-{hue}-{n}` | One per named hue |
| `--sf-dot-size-{size}` | `h-* w-*` | Diameter values |

Until the token migration lands, components consume the literal Tailwind classes directly. Adapters that need to theme the dot colours should use `className` override (the prop is forwarded to the outer span).

---

## 4. Visual states

ColorDot has a single visual state per configuration. There are no hover, focus, active, or disabled states.

| State | Trigger | Notes |
|---|---|---|
| Default | Always | Filled circle in chosen hue |
| Pulsing | `pulse={true}` | Continuously animating ring; static dot underneath |

---

## 5. Dark mode

The reference implementation uses direct Tailwind colour utilities (e.g., `bg-green-500`). These are fixed hues — they do NOT automatically invert in dark mode.

Dark mode behaviour is the host's responsibility for the fill hue; the `border-foreground` boundary
remains theme-aware. Hosts may apply a `dark:` variant class via `className`, or provide a provider
theme that overrides `--sf-dot-color-{hue}` tokens when they land.

---

## 6. Responsive behaviour

ColorDot has a fixed physical size set by the `size` prop. No responsive behaviour — dots do not change size by viewport.

---

## 7. Do / Don't

### Do

- Use `className` to override size or add spacing when a one-off layout is needed.
- Keep colour consistent within a given legend or category system.
- Use `pulse` only for genuinely live/active indicators to avoid attention noise.

### Don't

- Don't mix ColorDot's explicit hues with Badge's `variant` semantics without a clear mapping documented in the host.
- Don't remove the dot boundary; it is the non-text contrast treatment for light hues.
- Don't hard-code colour in component code — prefer `className` override until tokens land.
