# ColorDot — Accessibility Contract

- **Component:** ColorDot
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorDot.Semantic.md) · [Interaction](./ColorDot.Interaction.md) · [Accessibility](./ColorDot.Accessibility.md) · [Styling](./ColorDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/ColorDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ColorDot conveys meaning through colour alone, which is a direct WCAG 2.2 SC 1.4.1 Use of Color concern. This contract pins the requirements for accessible alternative signals and the correct ARIA role assignment.

---

## 2. Root element + role

| Scenario | Element | Role | Accessible name |
|---|---|---|---|
| Meaningful `label` or `aria-label` provided | `<span>` | `role="img"` | owned `aria-label` |
| Both labels absent or whitespace-only (decorative) | `<span>` | none (no role attribute) | none |

**When `label` is absent:** The dot is purely decorative — a visual swatch. SR users receive no announcement. This is only acceptable when the adjacent visible text carries the same information (e.g., "● Online" where "Online" text is rendered beside the dot).

**When `label` is provided:** SR reads the label text (e.g., "Live" or "Online"). The `role="img"` is correct for a self-contained graphical element with a text alternative.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color; SC 1.1.1 Non-text Content (for the `role="img"` path).

ColorDot owns its accessible name. `aria-labelledby` is not part of its passthrough surface, so it
cannot compete with the owned `aria-label`. When an equivalent visible label is adjacent, render a
decorative ColorDot without either owned label to avoid duplicate announcements.

---

## 3. Cross-channel signal requirement

ColorDot uses colour as its primary channel. The contract requires:

**RULE:** Wherever ColorDot is the sole conveyor of status/category meaning, the host MUST provide a text alternative either via:

1. The `label` prop on ColorDot (`<ColorDot label="Online" />`), OR
2. Adjacent visible text that names the same state (`<ColorDot /> Online`).

Failing both conditions means colour is the ONLY channel, violating WCAG 2.2 SC 1.4.1.

**Dev-mode guidance (not enforced in component code):** When `label` is absent AND the component is not inside a container with adjacent text, the host is responsible for accessibility. ColorDot does not emit a console warning for label-absent usage — the decision is context-dependent.

---

## 4. Pulse animation and reduced motion

The `pulse` animation is `animate-ping` (a continuous CSS animation). This motion MUST be suppressed when the user's OS requests reduced motion.

**REQUIREMENT:** The Tailwind class `animate-ping` on the pulse ring SHOULD be wrapped in a `motion-safe:animate-ping` utility (or the host's CSS layer MUST include `@media (prefers-reduced-motion: reduce) { .animate-ping { animation: none; } }`).

The pulse ring is `aria-hidden="true"` — it carries no AT signal regardless of motion setting.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions (not directly applicable for auto-animations, but aligned with best practice for indefinite CSS motion).

**Known gap:** The reference implementation uses bare `animate-ping` without the `motion-safe:` modifier. This is a known gap to close at M2.

---

## 5. Keyboard

ColorDot is not focusable. No keyboard contract applies.

When composed inside an interactive parent, the parent owns all keyboard behaviour.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard — ColorDot contributes no keyboard surface.

---

## 6. Touch target

ColorDot is non-interactive. Touch-target requirements do not apply at the ColorDot level.

---

## 7. Color contrast

The dot's hue must contrast against the surrounding background at minimum 3:1 per WCAG 2.2 SC 1.4.11 Non-text Contrast.

| Color | Tailwind class | Approximate contrast on white |
|---|---|---|
| `gray` | `bg-gray-400` | ~3.9:1 — passes |
| `red` | `bg-red-500` | ~3.7:1 — passes |
| `green` | `bg-green-500` | ~3.1:1 — marginal; verify |
| `yellow` | `bg-yellow-400` | ~1.7:1 — FAILS on white |
| `lime` | `bg-lime-500` | ~2.5:1 — FAILS on white |
| `cyan` | `bg-cyan-500` | ~3.2:1 — passes |

**Boundary rule:** Every dot renders a 1px `border-foreground` boundary, which supplies a
theme-aware 3:1 meaningful non-text edge for yellow, lime, and every other hue. In forced-colors
mode the dot and boundary use the `CanvasText` system color so the indicator remains perceivable.

---

## 8. Do / Don't

### Do

- Provide `label` whenever the dot is the sole indicator of the meaning.
- Use adjacent text as an alternative to `label` (e.g., colour legend rows); leave the dot decorative.
- Mark the pulse ring `aria-hidden="true"` (already implemented).
- Honour `prefers-reduced-motion` for the pulse animation (host responsibility until motion-safe class is added).

### Don't

- Don't rely on colour alone. The dot is not self-describing without `label` or adjacent text.
- Don't provide whitespace-only `label` or `aria-label` values.
- Don't use `aria-labelledby` on ColorDot; use a decorative dot beside externally labelled content.
- Don't set `tabIndex` on ColorDot — it is never interactive.
- Don't add `role="status"` to ColorDot — the dot is an image/decoration, not a status message region.

---

## 9. Known gaps

| # | Item | Resolution path |
|---|---|---|
| A1 | `animate-ping` lacks `motion-safe:` modifier | Add `motion-safe:animate-ping` in next M2 touch |
| A2 | `yellow` and `lime` fail 3:1 contrast on white | Resolved: theme-aware 1px boundary + forced-colors system color |
| A3 | No dev-mode warning when label absent + standalone | Optional: add at implementation |
