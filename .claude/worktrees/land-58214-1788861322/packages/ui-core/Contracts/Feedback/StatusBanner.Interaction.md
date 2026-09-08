# StatusBanner — Interaction Contract

- **Component:** StatusBanner
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction (behavior, state transitions, dismiss model)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StatusBanner.Semantic.md) · [Styling](./StatusBanner.Styling.md) · [Accessibility](./StatusBanner.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/StatusBanner.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract describes how StatusBanner responds to user interaction and
manages its own state transitions. Visual tokens and ARIA wiring are owned by
Styling and Accessibility (PAO).

StatusBanner is **externally-controlled**: the host decides whether the banner
is shown (by rendering it or not) and reacts to `onDismiss` to remove it.
StatusBanner has no internal dismissed/hidden state — it renders when mounted
and the host removes it from the tree in response to `onDismiss`.

---

## 2. Dismiss interaction

### 2.1 Triggering dismiss

When `dismissible === true` (by explicit prop or per-type default — see Semantic
§5), a dismiss button is rendered at the trailing edge of the banner.

The dismiss button fires `onDismiss()` on:

| Input | Trigger |
|---|---|
| Mouse click | `click` event on the dismiss button |
| Keyboard: Space | `keydown` on the focused dismiss button |
| Keyboard: Enter | `keydown` on the focused dismiss button |
| Keyboard: Escape | `keydown` anywhere on the banner or its dismiss button |

The Escape key fires `onDismiss()` when:
- Focus is inside the banner (on the dismiss button, or on any focusable child), **OR**
- The banner has `role="alert"` and is the most-recently-announced live region
  (browser-specific; not reliably implementable without explicit focus tracking).

For M1, implement Escape dismiss only when focus is on the dismiss button itself.
Global Escape (focus-independent) is deferred (§5 open questions).

### 2.2 Dismiss is host-owned

`onDismiss()` is a fire-and-forget callback. StatusBanner does **not**:

- Set internal state to hide itself.
- Animate out after dismiss (animation is a PAO Styling concern; see §3).
- Prevent re-display if the host re-mounts it.

The host MUST remove the banner from the render tree (or stop rendering it) in
response to `onDismiss` — if the host ignores the callback, the banner stays
visible.

### 2.3 When `dismissible === false`

No dismiss button is rendered. No keyboard dismiss path exists. The banner is
non-interactive (except for any interactive children in the `message` slot).

---

## 3. Animation

### 3.1 Enter animation (mount)

StatusBanner MAY animate on mount:

- Recommended: `fade-in` + slight `translate-y` (slides down 4–8px while fading
  in). Duration: `150ms ease-out`.
- Acceptable fallback: no animation (instant render).
- Under `prefers-reduced-motion: reduce`: render instantly with no animation.

### 3.2 Exit animation (before unmount)

Exit animation is the host's responsibility. StatusBanner does **not** manage
its own unmount timing or animation. Hosts that want an exit animation should:

1. On `onDismiss`, set a local "dismissing" flag.
2. Apply a CSS exit class to the banner (`opacity-0 translate-y-1 transition-opacity`).
3. After the transition duration, remove the banner from the tree.

StatusBanner does NOT expose an `exiting` prop or callback to drive this pattern.
Hosts implement it with their own state. This is the standard React approach for
exit animations without an animation library.

### 3.3 Reduced-motion compliance

All animations (enter and exit) MUST be suppressed under
`prefers-reduced-motion: reduce`. This means:

- Enter: render instantly (no fade, no slide).
- Exit: dismiss instantly (no opacity transition).

PAO Styling owns the token and Tailwind class recipe for this. Adapters must
apply `motion-reduce:transition-none motion-reduce:transform-none` (or the
equivalent CSS media query) to any animated wrapper.

---

## 4. Focus management

### 4.1 Focus after dismiss

When the dismiss button is activated and `onDismiss` fires:

1. If `dismissible === true` and a dismiss button exists: focus was on the dismiss
   button before activation. After the host removes the banner from the tree,
   focus is lost (the focused element is unmounted). The host SHOULD return focus
   to a meaningful element — typically the element that triggered the action that
   caused the banner to appear.

2. Adapter responsibility: the React adapter MUST NOT try to manage post-dismiss
   focus internally (it doesn't know which element to return to). The host uses
   a `ref` or focus-management library for this.

3. Per the Accessibility contract (§4), returning focus is a SHOULD (WCAG 2.2
   SC 3.2.2), not a MUST, for non-modal components like StatusBanner.

### 4.2 Focus within the banner

StatusBanner itself is not a focus container (no `role="dialog"`, no focus trap).
If the banner contains interactive content (e.g., a CTA link in the message), that
content is in the natural tab order. The dismiss button is a standard `<button>` in
the tab order.

Tab sequence within a dismissible banner: `[dismiss button]` (single tabstop,
typically at end; PAO Accessibility owns exact placement).

---

## 5. Open questions

1. **Global Escape dismiss (focus-independent).** M1 implements Escape dismiss
   only when the dismiss button has focus. A "close on Escape regardless of focus"
   pattern (per Material Design and Vaadin) requires a global keydown listener,
   which has document-scope implications. Defer to Phase M2 after usage patterns
   are known.

2. **Banner stack and dismiss ordering.** When multiple StatusBanners are rendered
   (e.g., in a notification area), should Escape dismiss the most-recently-added
   banner? This requires a stack-aware context that is not part of this contract.
   Each banner manages its own dismiss independently in M1.

3. **Auto-dismiss timer.** Some designs auto-dismiss `informational` banners after
   N seconds. This is not in M1 scope; the host implements it by calling
   `onDismiss()` from a `useEffect` timer. If demand emerges, a future
   `autoDismissAfterMs` prop could be added.

---

## 6. Do / Don't

### Do

- Call `onDismiss()` immediately when the dismiss trigger fires; do not debounce.
- Honor `prefers-reduced-motion: reduce` by skipping animations.
- Implement Escape dismiss when focus is on the dismiss button.
- Let the host own post-dismiss focus return.

### Don't

- Don't hide or unmount the banner internally — that's the host's job.
- Don't add a focus trap; StatusBanner is not a modal.
- Don't animate under `prefers-reduced-motion: reduce`.
- Don't render the dismiss button when `dismissible === false`, even if
  `onDismiss` is provided.

---

## References

- [StatusBanner.Semantic.md](./StatusBanner.Semantic.md) — props, type union, ARIA role table
- [StatusBanner.Accessibility.md](./StatusBanner.Accessibility.md) — WCAG requirements, keyboard model
- [StatusBanner.Styling.md](./StatusBanner.Styling.md) — `--sf-banner-*` token surface, animation tokens
- ADR 0017 §A1.2 — StatusBanner 4-contract spec
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 3.2.2 On Input
