# ColorDot — Interaction Contract

- **Component:** ColorDot
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorDot.Semantic.md) · [Interaction](./ColorDot.Interaction.md) · [Accessibility](./ColorDot.Accessibility.md) · [Styling](./ColorDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/ColorDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

ColorDot is **non-interactive**. This contract documents the full interaction surface (which is intentionally minimal) and establishes the boundary with host-supplied interactivity.

---

## 2. No interaction surface

ColorDot does **not**:

- Fire `onClick`, `onFocus`, `onBlur`, or any other event callback.
- Maintain internal state.
- Respond to keyboard input.
- Participate in tab order — the root `<span>` is never focusable.

The `pulse` prop animates the ring but does not represent a state machine — it is an unconditional CSS animation (`animate-ping`) that runs whenever `pulse={true}`. No "start / stop" event is emitted.

---

## 3. Pulse animation

The `pulse` feature is a pure CSS effect: an `absolute`-positioned `<span>` with `animate-ping` opacity-75. Its only behavioural implication is visual motion.

| `pulse` value | Visual behaviour |
|---|---|
| `false` (default) | Static dot, no animation |
| `true` | Outer ring pulses continuously via `animate-ping` |

The pulsing ring is `aria-hidden="true"` — it carries no semantics; it only signals "live" to sighted users. AT users rely on the `label` prop (e.g., `label="Live"`) to understand the same intent.

---

## 4. Hover / cursor

ColorDot applies no hover style and no `cursor: pointer`. It is a non-interactive indicator. A pointer cursor would falsely advertise clickability.

When a host wraps ColorDot inside an interactive parent (`<button>`, `<a>`, etc.), the wrapper's hover and cursor styles govern (CSS cascade).

---

## 5. Keyboard behaviour

ColorDot contributes no tab stop and has no keyboard handlers.

---

## 6. State machine

ColorDot has no internal state machine. It renders deterministically from its props. There is no loading, selected, hovered, focused, or disabled state.

---

## 7. Known gaps

| # | Gap | Recommended host pattern |
|---|---|---|
| G1 | No click/toggle interaction built in | Wrap in `<button>` with `onClick`; ColorDot is the visual content |
| G2 | No disabled/muted visual state prop | Apply `opacity-50` via `className` for muted appearance |
| G3 | Pulse cannot be paused programmatically without prop change | Parent controls `pulse` prop; no internal timer |
