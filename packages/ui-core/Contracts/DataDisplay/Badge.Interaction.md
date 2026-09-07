# Badge — Interaction Contract

- **Component:** Badge
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Badge.Semantic.md) · [Styling](./Badge.Styling.md) · [Accessibility](./Badge.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/badges/Badge.tsx`
- **Catalog row:** #9 Badge (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

Badge is **presentational and stateless**. This contract documents the
interaction surface for completeness — there are no internal state machines,
no event callbacks, and no input handlers in the M2 baseline. The contract
also clarifies the boundary with future interactive variants (Chip) so hosts
do not over-extend Badge.

---

## 2. No interaction surface

Badge does **not**:

- Fire `onClick`, `onFocus`, `onBlur`, `onKeyDown`, or any other callback
  as part of its public surface.
- Maintain internal state (open / closed, hovered, selected, etc.).
- Respond to keyboard input.
- Participate in tab order — the rendered `<span>` is not focusable by
  default.

The interaction-shape is **the inverse of Button**: Badge is a marker, not
an actuator.

---

## 3. Hover / cursor

The default cursor over a Badge is the normal pointer (text cursor when
inside text content, or default arrow when standalone). Badge does **not**
apply a `cursor: pointer` treatment — that would falsely advertise
interactivity. PAO Styling honours this constraint.

When a host wraps Badge inside an interactive element (Button, `<a>`, custom
clickable), the wrapper's hover/cursor wins (CSS cascade).

---

## 4. HTML-attribute pass-through and accidental interactivity

Badge's semantic contract (§3.4) supports HTML attribute passthrough on the
root `<span>`. This means a host can pass `onClick`, `role="button"`,
`tabIndex`, etc., and the native browser will honour them.

This contract takes **no position** on whether such host-applied
interactivity is supported behaviour. The recommended pattern is:

- For "click on a status marker" → wrap Badge in Button or a real anchor.
- For "filter chip" → use the future Chip component (catalog #25).
- Direct `onClick` on Badge via passthrough is **not advertised** behaviour;
  hosts who do it own the accessibility and styling consequences.

---

## 5. Keyboard behaviour

Badge does **not** contribute a tab-stop. There are no keyboard handlers in
M2.

If a host applies `tabIndex="0"` via passthrough, the element becomes
focusable but still has no keyboard activation handlers — pressing Enter
or Space does nothing. This is by design; clickable badge semantics belong
to Chip (future) or a wrapping Button.

---

## 6. Variant / size / appearance changes

Changes to `variant`, `size`, or `appearance` props are pure re-renders.
There are no internal animations or transitions defined at the contract
level (PAO Styling may add a colour-token transition, but the behavioural
contract does not require one).

A Badge whose `variant` changes from `'warning'` to `'success'` (e.g. an
invoice transitioning from "Pending" → "Paid") simply re-renders with the
new tokens. Hosts who want the change to be animated must wrap externally.

---

## 7. Loading / disabled state

Badge has **no loading state**. Badge has **no disabled state**. Both
concepts belong to interactive components (Button, form fields) — Badge is
neither.

If a host needs to communicate "this badge is greyed out" (e.g. a vendor's
status is not yet known), the host renders Badge with
`variant="neutral"` and the appropriate `children` text ("Unknown",
"—"). No `disabled` prop.

---

## 8. Interaction-state precedence

Badge has effectively one state — its current render — and no internal
state transitions to order. This section exists only to mirror the
contract template; there are no precedence rules to specify.

---

## 9. Council open questions (Interaction)

1. **Should Badge document a `role` recommendation?** PAO Accessibility
   may want hosts to set `role="status"` for live status badges (so AT
   announces transitions) and leave it absent for static labels. The
   distinction is interaction-adjacent. Confirm whether this contract
   should reference it or leave it entirely to PAO Accessibility.
2. **Clickable-badge escape hatch.** Some teams want a minimal opt-in
   `interactive?: boolean` that wires `role="button"` + `tabIndex={0}`
   + Enter/Space handlers. Leaning **no** — escalates to Chip; keeps
   Badge clean.
3. **Live-region behaviour on variant change.** When a Badge's variant
   changes (e.g. status transitions), should it default to announcing
   via `aria-live`? Today: no. PAO Accessibility decides; this contract
   defers.
