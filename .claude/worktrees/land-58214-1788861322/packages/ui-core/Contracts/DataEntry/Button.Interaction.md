# Button — Interaction Contract

- **Component:** Button
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Button.Semantic.md) · [Styling](./Button.Styling.md) · [Accessibility](./Button.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/Button.tsx`
- **Catalog row:** #17 Button (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes **how Button behaves in response to user input** —
activation triggers, the disabled / loading interlock, and the interaction
boundary established by the `asChild` Slot composition. It references the
prop and event shapes defined in the [Semantic contract](./Button.Semantic.md);
it does not restate them. Visual styling (focus rings, hover states) and ARIA
wiring (icon-button labels, `aria-busy` on loading) are owned by the Styling
and Accessibility contracts (PAO).

---

## 2. Activation

- **Triggers:**
  - Mouse / touch — primary-button click on the rendered element.
  - Keyboard — Enter or Space when the button has focus (native `<button>`
    behaviour).
- **Callback:** fires `onClick(event)` exactly once per activation. The
  payload is the React synthetic `MouseEvent<HTMLButtonElement>` (or, when
  `asChild` is used, whatever event the rendered element emits — typically
  identical).
- **`preventDefault` / `stopPropagation`:** the host may invoke either on
  the forwarded event. Button does not internally consume the event.
- **Multi-fire protection:** Button does **not** internally debounce or
  single-fire-protect `onClick`. Hosts that need such semantics must
  enforce them in the callback (typical pattern: set local `loading`,
  pass it back as the `loading` prop — Button then suppresses subsequent
  activations until loading clears).

---

## 3. Disabled state

- **Trigger:** `disabled === true`.
- **Activation suppressed:** the native `disabled` attribute prevents all
  pointer + keyboard activation; `onClick` does not fire.
- **Focusability:** native `<button disabled>` is **not** focusable by
  default — this is the M2 baseline behaviour. PAO Accessibility owns the
  decision about whether to swap to `aria-disabled="true"` + a focusable-
  but-non-activatable treatment for keyboard discoverability. Until that
  decision lands, hosts that need to communicate "button exists but is
  currently unavailable" without removing it from tab order must wrap
  Button externally.
- **`asChild` propagation:** when `asChild === true`, the Slot pattern
  forwards `disabled` onto the child element. For an `<a>` rendered via
  `asChild`, "disabled" has no native semantics; the child receives
  `data-disabled` and `aria-disabled="true"` instead. PAO Accessibility
  finalises the attribute set.

---

## 4. Loading state

- **Trigger:** `loading === true`.
- **Activation suppressed:** Button internally treats `loading` as
  `disabled === true` for input purposes — `onClick` does not fire, the
  element does not respond to pointer or keyboard activation.
- **Visual indicator:** a spinner replaces the `leadingIcon` slot, or is
  inserted before `children` when no `leadingIcon` is supplied. The
  `children` text remains visible.
- **`aria-busy`:** PAO Accessibility sets `aria-busy="true"` on the rendered
  element during loading. Behavioural impact (announce-to-AT) is owned by
  that contract; this contract only requires the input-suppression
  behaviour.
- **Exit:** when `loading` returns to `false`, the spinner is removed,
  `leadingIcon` re-renders if supplied, and activation resumes.
- **`disabled` + `loading` simultaneously.** Both true: button is
  non-activatable; the spinner renders; the disabled visual treatment may
  or may not stack with the loading treatment (PAO Styling resolves).

---

## 5. `asChild` composition behaviour

When `asChild === true`:

- Button does **not** render its own `<button>`. Radix `Slot` composes
  styling + handlers + state onto the single `children` element.
- The child element's **native interaction semantics** apply:
  - `<a href="…">` activates via click or Enter (no Space) and respects
    `target`, `download`, etc.
  - A custom component that renders a `<button>` internally behaves
    identically to a non-`asChild` Button.
- The Button's `onClick` is **forwarded to the child** — fires when the
  child element is activated.
- The Button's `type` prop is **ignored**. The child's own type semantics
  win (`<a>` has none; a custom button-rendering child has its own).
- The Button's `disabled` / `loading` interlock still applies: when either
  is `true`, Button refuses to fire `onClick` on the child even if the
  child's native semantics would otherwise activate. PAO Accessibility
  owns the visual + AT treatment for the disabled link case.

The single-child constraint is enforced by Radix `Slot` — passing multiple
children or a fragment raises a runtime error.

---

## 6. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Moves focus to / from Button (single tab-stop, per native `<button>`). |
| Enter | Activates Button — fires `onClick`. (`asChild` + `<a>`: also activates per anchor semantics.) |
| Space | Activates Button — fires `onClick`. (`asChild` + `<a>`: Space does **not** activate an anchor by default; PAO Accessibility decides whether to polyfill.) |

Button adds no custom keyboard handlers beyond what native `<button>` (or
the `asChild` target) provides.

---

## 7. Focus behaviour

- Button uses **native browser focus** — clicking or tabbing places focus
  on the element. No internal focus management.
- After activation, focus remains on Button unless the host's `onClick`
  callback moves it (e.g. opening a dialog moves focus into the dialog
  per Dialog's own contract).
- When `disabled === true`, Button is not focusable (per native
  `<button disabled>`); when focus was already on Button and `disabled`
  flips to `true`, the browser moves focus to the document body (native
  behaviour).
- `loading === true` does **not** by itself remove focus — the button
  remains focusable while loading (so the user can see what they were
  about to activate). Activation is suppressed (§4).

---

## 8. Form interaction

- `type="submit"` + nested inside a `<form>` → activation triggers the
  form's submit handler (native).
- `type="reset"` + nested inside a `<form>` → activation resets the form
  (native).
- `type="button"` (default) → no form interaction; `onClick` is the only
  effect.
- `disabled` and `loading` both suppress form-submit triggering (because
  they suppress activation).

The `formAction`, `formMethod`, `formNoValidate`, `formTarget` HTML
attributes are supported via passthrough (Semantic §3.4). They take effect
only when `type="submit"`.

---

## 9. Interaction-state precedence

When multiple state props apply simultaneously, resolve in this order
(highest precedence first):

1. **`disabled === true`** — no activation, not focusable (native), no
   spinner shown (unless `loading` is also true).
2. **`loading === true`** — no activation, focusable, spinner shown,
   `aria-busy="true"`.
3. **`disabled && loading`** — both apply: not activatable; PAO
   resolves the visual stacking.
4. **Normal** — activatable, focusable, no spinner.

---

## 10. Council open questions (Interaction)

1. **Disabled-button focusability.** Native `<button disabled>` removes
   the element from the tab order. For keyboard users, a button that "is
   there but unavailable" is often invisible. Should we swap to
   `aria-disabled="true"` + a focusable-but-non-activatable treatment as
   the M2 default? Leaning: **defer to PAO Accessibility**; this is an
   ARIA-pattern decision that belongs in that contract.
2. **Space on `asChild` + `<a>`.** Anchors don't natively activate on
   Space. Should Button polyfill Space-to-click when `asChild` wraps an
   `<a>`? Leaning yes (so the button-shaped element behaves like a
   button regardless of underlying element), but it's a small footgun
   if the host's `<a>` already binds Space.
3. **Activation while `loading`.** Should we queue the activation and
   fire it when `loading` clears? Or hard-suppress (this spec)? Leaning
   hard-suppress — queueing is surprising and rarely what hosts want.
4. **Double-click protection.** Should Button internally debounce rapid
   double-activations (e.g. via a small `pending` micro-window after
   `onClick` fires)? Today: no — hosts handle via `loading`. Confirm
   no internal debounce.
5. **`onClick` async return value.** If `onClick` returns a Promise,
   should Button auto-set `loading` for the duration? This would be a
   convenience feature. Today: no auto-detection. Leaning keep manual —
   matches every other React button library and avoids surprise.
