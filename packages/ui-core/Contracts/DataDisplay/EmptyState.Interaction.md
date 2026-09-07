# EmptyState — Interaction Contract

- **Component:** EmptyState
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Styling](./EmptyState.Styling.md) · [Accessibility](./EmptyState.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** A18 EmptyState (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract describes the **interaction surface** of EmptyState. EmptyState is
primarily a presentational component; its interaction surface is small —
limited to the optional CTA button — but the contract documents how the
component behaves when present in a focus-and-keyboard context. Visual
styling and ARIA wiring are owned by the Styling and Accessibility contracts
(PAO).

---

## 2. CTA button (when `action` is supplied)

- **Trigger:** clicking the rendered `<button>`, or Enter/Space when the button
  has keyboard focus (standard native `<button type="button">` behaviour).
- **Callback:** fires `action.onClick()` exactly once per activation. The
  callback is the host's responsibility — typical implementations open a
  create-modal, navigate to a new-entity route, or call a creation mutation.
- **No-loading state.** M1 EmptyState does not own a "submitting" indicator.
  Hosts that want a busy/loading treatment on the CTA after activation must
  swap the EmptyState's children themselves (e.g. by re-rendering with a
  different `title`/`description`) — EmptyState does not provide it
  internally.
- **Re-activation.** EmptyState does not internally disable the CTA after
  click. Hosts that need debounce / single-fire semantics must enforce them
  in `action.onClick`.

---

## 3. No-action state (no CTA)

When `action` is **omitted**:

- The CTA button is not rendered.
- EmptyState has **no interactive surface** — it is entirely presentational.
- Focus passes through the component as if it were static text (no element
  inside is keyboard-focusable except natively-focusable links a host might
  embed via the `description` prop — but `description` is `string`, not
  `ReactNode`, so embedded links are not possible in M1).

---

## 4. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Move focus into / out of the CTA button when present. The component contributes a single tabstop. |
| Enter / Space (on CTA) | Activate the CTA — fires `action.onClick()`. |

EmptyState adds no custom keyboard handlers.

---

## 5. Interaction-state precedence

EmptyState has effectively two states:

1. **Actionable** (`action` is supplied) — CTA button is rendered and
   focusable; activation fires the callback.
2. **Presentational** (`action` omitted) — no interactive descendants; no
   keyboard tab-stop; no callbacks.

The `variant` prop has **no** behavioural effect — it changes only the icon
(and Styling-owned colour). Specifically, `variant === 'actionable'` does not
auto-imply that `action` must be present; a host can render
`<EmptyState variant="actionable" title="…" />` without an `action` and the
component renders fine without a CTA. (See Semantic open question #3 for
whether to tighten this.)

---

## 6. Council open questions (Interaction)

1. **Variant ↔ action coupling.** Should the component enforce
   `variant === 'actionable'` requires an `action`? Today the two are
   independent. Tightening would catch hosts who set `actionable` and
   forget the CTA, but might be too prescriptive. (Leaning: stay loose;
   the icon alone communicates "act here" usefully even without a button.)
2. **CTA disabled state.** Should `action` carry a `disabled?: boolean`
   field for hosts who want to render the CTA-shape but disable it (e.g.
   while permissions are loading)? Today no such field exists.
3. **Multiple-fire protection.** Some host code wants the CTA to fire at
   most once until re-rendered. Should EmptyState bake in single-fire
   semantics, or leave it to the host? (Leaning: leave to the host —
   bake-in would be a surprise for any host that wants repeat-fire.)
