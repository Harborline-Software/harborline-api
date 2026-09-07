# ColorDot — Semantic Contract

- **Component:** ColorDot
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ColorDot.Interaction.md) · [Accessibility](./ColorDot.Accessibility.md) · [Styling](./ColorDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/ColorDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` colored circle

---

## 1. Purpose

ColorDot is a small circular indicator that conveys categorical colour meaning — not a status badge, not an icon, but a raw hue marker. It is the canonical primitive for representing colour-coded entities (e.g., a calendar category, a tag swatch, a pipeline stage colour) when the colour itself is the data.

ColorDot is distinct from Badge in one key way: Badge conveys a semantic variant (`success`, `warning`, etc.) backed by the design token surface; ColorDot conveys an **explicit named hue** chosen by the host. The host knows "this category is orange"; it does not know if orange means "warning" or "feature branch" — that semantic is the host's responsibility.

ColorDot is **presentational and stateless**. Its only optional interaction-adjacent feature is the `pulse` animation (live indicator), which is purely visual.

Primary usage positions:

- **Colour legend swatch** next to a category label.
- **Status dot** beside an entity name in a table row.
- **Live activity indicator** (pulsing dot) on a realtime dashboard.
- **Composed into Tag or Badge** as a colour-coded swatch prefix.

---

## 2. Data model

ColorDot has no internal data model.

```typescript
type ColorDotColor =
  | 'gray' | 'red' | 'orange' | 'amber' | 'yellow'
  | 'lime' | 'green' | 'teal' | 'cyan' | 'blue'
  | 'indigo' | 'violet' | 'purple' | 'pink'

type ColorDotSize = 'xs' | 'sm' | 'md' | 'lg'

interface ColorDotProps {
  color?: ColorDotColor
  size?: ColorDotSize
  label?: string                // AT-only accessible name (renders no visible text); maps to aria-label
  'aria-label'?: string         // alternative to label; takes precedence when meaningful (see §3.4)
  pulse?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `color` | `ColorDotColor` | `'gray'` | The hue of the dot. 14 named colours; each maps to a specific Tailwind `bg-*-{n}` class. |
| `size` | `ColorDotSize` | `'md'` | Physical size of the dot: `xs` (6px), `sm` (8px), `md` (10px), `lg` (12px). |
| `label` | `string` | `undefined` | AT-only accessible name. **Does NOT render visible text.** If provided, sets `role="img"` and `aria-label` on the root span. If omitted AND `aria-label` is also absent, dot is decorative (no role, no aria-label). |
| `aria-label` | `string` | `undefined` | Explicit `aria-label` override. Preferred when the accessible name needs to differ from any `label` value, or when composing ColorDot inside a host that manages its own `aria-label` surface. Takes precedence over `label` when both present. |
| `pulse` | `boolean` | `false` | When `true`, renders an `animate-ping` ring (same hue) behind the dot — the standard "live" or "activity" indicator. The ring is `aria-hidden`. |
| `className` | `string` | `undefined` | Additional Tailwind classes merged onto the outer `<span>` via `cn()`. |

### 3.1 Color semantics

The 14 hue names are intentionally **non-semantic**: `red` is not "danger", `green` is not "success". The host assigns meaning. This is the primary differentiator from Badge's `variant` system.

If a host needs colour + semantic meaning, it should compose ColorDot inside a Badge or provide its own accessible label that states the meaning explicitly (e.g., `label="Online"`).

### 3.2 Size semantics

| Size | Rendered diameter | Typical use |
|---|---|---|
| `xs` | ~6px (`h-1.5 w-1.5`) | Dense table cells; stacked list indicators |
| `sm` | ~8px (`h-2 w-2`) | Inline beside labels |
| `md` | ~10px (`h-2.5 w-2.5`) | Default; standard indicator size |
| `lg` | ~12px (`h-3 w-3`) | Legend swatches; prominent indicators |

### 3.3 HTML attribute passthrough

ColorDot forwards standard HTML attributes to the root span. It owns `role`, `aria-label`, and
`aria-labelledby`: callers cannot override those attributes through passthrough. This keeps its
accessible-name algorithm deterministic while retaining ordinary DOM attributes such as `id` and
`data-*` for host composition.

### 3.4 Element choice and aria-label resolution

ColorDot renders as:
- `<span role="img" aria-label={ariaLabel}>` when `aria-label` OR `label` is provided (the AT-accessible path)
- Bare `<span>` (no role, no aria-label) when both are absent (the decorative path)

Precedence: a meaningful (non-whitespace) `aria-label` prop wins over a meaningful `label` prop.
Whitespace-only values are treated as absent, so they cannot create a practically empty accessible
name or erase a meaningful fallback label. `aria-labelledby` is intentionally unsupported because
ColorDot owns its accessible name; wrap the decorative dot with externally labelled content instead.

**`label` vs `aria-label`:** The `label` prop predates the `aria-label` prop and is kept for backward compatibility and conciseness. Hosts who prefer explicitness can use `aria-label` directly. Both do the same thing: set `role="img"` and provide the AT name. (Resolves Open Question #2: single-prop ergonomics kept via `label`; explicit override available via `aria-label`.)

---

## 4. Events — semantics

ColorDot has **no events**. It is presentational and emits no callbacks.

---

## 5. Slots

ColorDot has no slot props. The dot renders entirely from its props.

---

## 6. Component composition

- **Legend row:** `<ColorDot color="blue" size="sm" label="Feature" /> Feature` — colour swatch with adjacent label text.
- **Table cell status:** `<ColorDot color="green" label="Online" />` in a DataGrid cell. The `label` prop satisfies the accessible name requirement.
- **Live dashboard:** `<ColorDot color="green" pulse label="Live" />` — animated ring indicates active connection.
- **Inside Tag:** Tag's `icon` prop accepts a ColorDot as a leading swatch.
- **Inside Badge:** Badge's `leadingIcon` slot accepts a ColorDot for colour-coded badges.

---

## 7. Deferred features

- **Pattern fills** — hatching or stripes for colour-blind differentiation. Deferred; use `label` for AT until a fill-pattern prop arrives.
- **Custom colour** — a hex/CSS-variable prop for out-of-palette hues. Deferred; use `className` override.
- **onClick / interactive dot** — a clickable dot for filter toggling. Hosts compose inside a `<button>` until a dedicated interactive variant exists.
- **Tooltip composition** — ColorDot does not own tooltip; hosts wrap externally.

---

## 8. Open questions

1. **14-hue palette completeness.** The current palette covers common Tailwind swatches. Does the product need `rose` (distinct from `red` + `pink`)? Confirm with PAO.
2. ~~**`label` vs `aria-label` separation.**~~ [RESOLVED 2026-06-06] §3.4: `label` prop kept for ergonomics; explicit `aria-label` prop added as override. Both set `role="img"` + `aria-label`. Tooltip remains host responsibility.
3. **Pulse intensity.** `animate-ping` at `opacity-75` may be too aggressive for enterprise contexts. PAO Styling may want a `pulse="subtle" | "strong"` axis.
