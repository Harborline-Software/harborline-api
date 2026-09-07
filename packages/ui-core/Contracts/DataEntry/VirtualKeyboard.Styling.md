# VirtualKeyboard — Styling Contract

- **Component:** VirtualKeyboard
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./VirtualKeyboard.Semantic.md) · [Interaction](./VirtualKeyboard.Interaction.md) · [Accessibility](./VirtualKeyboard.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/VirtualKeyboard.tsx` (not yet implemented)
- **Catalog row:** #A40 VirtualKeyboard (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Zero runtime style injection

The component **never** touches `document.head`. No `<link>` element is created, no stylesheet is
fetched, no `<style>` tag is appended.

The prototype (`tooling/translation-review/src/VirtualKeyboard.tsx`) injected a stylesheet link at
mount. That pattern is disallowed in `@harborline-software/ui-react`: it escapes the package's style
boundary, races React's render, breaks under SSR, and leaks a global element the component cannot
reliably remove. All styling here is token utilities on the component's own elements, shipped in
`packages/ui-react/src/style.css` like every other component in the package.

---

## 2. Tokens only

Every colour comes from a semantic token utility. No raw hex, no `bg-slate-200`, no `#333`. This is
the `token-discipline-lint` rule (`tooling/token-discipline-lint/`), and the component carries no
exemption.

| Surface | Token utility | Role |
|---|---|---|
| Panel background | `bg-muted` | The keyboard's tray, distinct from page canvas |
| Panel border | `border-border` | The tray edge |
| Key face | `bg-background` | Keys read as raised against the tray |
| Key border | `border-border` | Key edge |
| Key glyph | `text-foreground` | The character |
| Special-key glyph | `text-muted-foreground` | Shift / Backspace / Tab read quieter than characters |
| Pressed / active toggle | `bg-accent` + `text-accent-foreground` | Shift and Caps while live |
| Focus ring | `ring-ring` + `ring-offset-background` | §7 |
| Advisory text | `text-muted-foreground` | §6 |
| Disabled | `opacity-50` (no colour change) | Preserves token contrast relationships |

**Light and dark both come free** because every token above is themed. There is no dark-mode
branch in the component and no `dark:` variant in its classes — a `dark:` override would fork the
palette the tokens exist to keep single.

---

## 3. Size variants

| `size` | Key height | Key type scale | Gap |
|---|---|---|---|
| `sm` | `h-8` | `text-xs` | `gap-1` |
| `md` (default) | `h-10` | `text-sm` | `gap-1.5` |
| `touch` | `h-11 min-w-11` | `text-sm` | `gap-2` |

`touch` floors the hit area at 44px (WCAG 2.5.8) and widens the gap, because 44px targets still need
≥8px separation to be independently tappable. The type scale does **not** grow with `touch` — the
box grows, the glyph stays put, matching `SegmentedControl`'s established convention.

Under `useTouchSizing()` (Interaction §7), `sm` and `md` receive the `touchTarget` floor class
additively; their type scale is untouched. A caller's requested density is never silently upgraded
in its *visual* dimension, only in its *hit* dimension.

---

## 4. Toggle-key visual state

| State | Classes |
|---|---|
| Shift live (one-shot or locked) | `bg-accent text-accent-foreground` |
| Caps locked | `bg-accent text-accent-foreground` **+** a persistent indicator (a filled dot or underline in `currentColor`) |
| Neither | key default (§2) |

The lock indicator is what visually separates a sticky lock from a one-shot shift. It is drawn in
`currentColor` rather than a colour token so it inherits the pressed-state foreground and cannot
drift out of contrast with it.

---

## 5. Mobile-first layout — the load-bearing requirement

**The panel is fluid and must produce zero horizontal overflow at 320px.** This is the acceptance
criterion, not an aspiration; the wave-10 clipping regression this component's promotion followed
was exactly a fixed-width grid overflowing a phone viewport.

| Rule | Requirement |
|---|---|
| Container width | `w-full max-w-full` — never a fixed `w-[…]`, never a `min-width` on the panel |
| Rows | `flex` with `flex-wrap`, so a row that cannot fit reflows to a second visual line rather than clipping or forcing a scrollbar |
| Keys | `flex-1` with a `basis-0` seed, so keys share the available width evenly and shrink together |
| Key minimum | `min-w-0` on the flex child, so text content cannot establish a floor that forces overflow — the one omission that most commonly causes this exact bug |
| Wide keys | Space and Backspace use a `flex-grow` multiplier, never a fixed width |
| Overflow | `overflow-x` is **never** set to `auto` or `scroll` on the panel. A horizontal scrollbar here is a bug being hidden, not a feature. |
| Box sizing | Padding and borders inside the width budget (`box-border`, the package default) |

### 5.1 Responsive gating

Any behaviour that must change by viewport is **JS-gated** via `useMediaQuery` /
`useFormFactor`, per the house doctrine enforced by `small-screen-lint`'s `SS-JS-GATE` rule. Bare
Tailwind screen variants (`md:`, `lg:`) are not used for layout toggles. Container queries (`@md:`)
are permitted where the panel should respond to *its own* box rather than the viewport, which is
the more correct mechanism for a component that may be docked into a narrow side panel on a wide
screen.

### 5.2 Logical properties only

All directional styling uses logical properties — `ps-*`/`pe-*`, `ms-*`/`me-*`, `start`/`end`,
`text-start` — never `pl-*`/`pr-*`/`left`/`right`/`text-left`. This is what makes Accessibility §5's
RTL guarantee real for `ar` and `ur` instead of merely declared, and it is the `css-logical-audit`
tooling's rule.

### 5.3 Acceptance test

The implementation card ships a viewport check at **320 / 360 / 390 / 430 px**, in light and dark,
for at least one LTR layout (`english`), one RTL layout (`arabic`), and one dense-glyph layout
(`bengali`). Passing means, at every one of those widths:

1. `document.documentElement.scrollWidth <= clientWidth` — no horizontal overflow;
2. every key's rendered box is fully inside the panel's content box — no clipped keys;
3. every key's hit area is ≥44px in both dimensions under `touch` / `useTouchSizing()`;
4. no key's glyph is truncated or ellipsised.

320px is the floor because it is the narrowest viewport the design language supports; 360/390/430
are the common phone widths where the wave-10 clipping actually appeared.

---

## 6. IME advisory

The advisory paragraph (Semantic §6, Accessibility §7) sits **above** the key grid inside the panel,
so it is read before the keys in both reading order and visual order.

`text-xs text-muted-foreground` with the panel's inline padding, and `text-balance` so a two-line
advisory breaks evenly rather than leaving one orphaned word. It is quiet: an advisory that shouts
gets dismissed as chrome, and this one carries information the person needs exactly once.

---

## 7. Focus visibility

Every key shows a visible focus ring on `:focus-visible`:
`ring-2 ring-ring ring-offset-2 ring-offset-background outline-none`.

`ring-offset-background` (not `ring-offset-muted`) is deliberate: the offset must match what is
*behind* the ring, and keys sit on the panel's `bg-muted` tray — so the implementation applies the
offset colour of the tray at the key level. Getting this wrong produces a halo that looks like a
rendering artifact.

Because keys are dense and adjacent (§3), the ring must be drawn **outside** the key box
(`ring-offset`) rather than inset, or a focused key's ring is indistinguishable from its neighbour's
border.

---

## 8. Motion

Key press feedback is a `transition-colors duration-75` background change. No transform, no scale,
no bounce.

A scale animation on a key under a finger moves the target out from under the finger mid-press,
and at ~45 simultaneous elements any transform animation is a compositing cost paid on the exact
devices least able to pay it. The press feedback that matters is the character appearing in the
field — this is the smallest confirmation that reads as responsive without competing with it.

`prefers-reduced-motion: reduce` removes the transition entirely; the colour change remains
instantaneous, so no feedback is lost.
