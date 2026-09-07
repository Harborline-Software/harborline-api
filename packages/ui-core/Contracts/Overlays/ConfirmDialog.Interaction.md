# ConfirmDialog — Interaction Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Styling](./ConfirmDialog.Styling.md) · [Accessibility](./ConfirmDialog.Accessibility.md)
- **Composes:** [Dialog.Interaction.md](./Dialog.Interaction.md)
- **Reference implementation:** `packages/ui-react/src/components/dialogs/ConfirmDialog.tsx`
- **Catalog row:** (no Telerik counterpart — ConfirmDialog is a Harborline-native composition)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

ConfirmDialog inherits all of Dialog's interaction behaviour (focus trap,
scroll lock, close gestures, keyboard handling). This contract documents the
ConfirmDialog-specific behaviour: the Cancel and Confirm button interactions,
the auto-close on Confirm, and the variant treatments.

Visual styling and ARIA wiring are owned by Styling and Accessibility (PAO).
For the underlying Dialog's behaviour see [Dialog.Interaction.md](./Dialog.Interaction.md).

---

## 2. Cancel button

- **Trigger:** click, or focus + Enter / Space.
- **Behaviour:** calls `onOpenChange(false)`. Does **not** call `onConfirm`.
- **Visual:** outlined neutral button (`border-gray-300 bg-white text-gray-700`).
  Tokens owned by PAO Styling.
- **Position:** left of the Confirm button in the footer (the order in
  which React renders the footer slot).

---

## 3. Confirm button

- **Trigger:** click, or focus + Enter / Space.
- **Behaviour:** calls `onConfirm()` **then** `onOpenChange(false)`. The
  order is: synchronous call to `onConfirm` runs first, then synchronous
  call to `onOpenChange(false)` closes the dialog.
- **Async note:** if `onConfirm` is async (returns a Promise), the dialog
  closes immediately — it does not await. Hosts that need to keep the
  dialog open during in-flight work must use Dialog directly or wait for a
  future `loading` prop (Semantic §7).
- **Visual:**
  - `variant === 'default'` (default): blue
    (`bg-blue-600 hover:bg-blue-700 focus:ring-blue-500`).
  - `variant === 'destructive'`: red
    (`bg-red-600 hover:bg-red-700 focus:ring-red-500`).
  Tokens owned by PAO Styling.
- **Position:** right of the Cancel button.

---

## 4. Inherited Dialog behaviour

The following are inherited from Dialog (see Dialog.Interaction.md for
detail):

- **Focus trap** — focus cycles among Cancel button, Confirm button, close
  button (top-right `X`). The body region is empty so there are no other
  focusable descendants.
- **Initial focus** — Radix-default; typically lands on the close button
  (first focusable element in the header).
- **Close gestures** — overlay click, Escape key, close-button click — all
  fire `onOpenChange(false)` and do **not** fire `onConfirm`. Functionally
  these are equivalent to clicking Cancel.
- **Body scroll lock** — yes, while open.
- **Return focus** — to the element that had focus before open.

---

## 5. Keyboard behaviour

Inherits Dialog's matrix plus:

| Key | Behaviour |
| --- | --- |
| Enter (Cancel button focused) | Activates Cancel — fires `onOpenChange(false)`. |
| Enter (Confirm button focused) | Activates Confirm — fires `onConfirm` then `onOpenChange(false)`. |
| Enter (body, since body is empty) | No effect from ConfirmDialog. (Browser may submit an enclosing form if the dialog is nested in one — but ConfirmDialog has no internal form.) |
| Escape | Fires `onOpenChange(false)` — same as Cancel. |
| Tab / Shift+Tab | Cycles focus among close button → Cancel → Confirm → close button. |

There is **no default-button enter-key submit** in M1. The user must
explicitly Tab to the Confirm button before pressing Enter activates it.
(Some hosts expect Enter to confirm regardless of which button has focus;
that is a deferred enhancement.)

---

## 6. Interaction-state precedence

ConfirmDialog has the same two macro states as Dialog (Open / Closed). The
variant prop affects visual treatment only, not behaviour.

When Confirm is activated, the sequence is:

1. `onConfirm()` runs (synchronous portion only).
2. `onOpenChange(false)` runs.
3. Radix executes the exit animation and unmounts the portal.
4. Focus returns to the pre-open element.

---

## 7. Council open questions (Interaction)

1. **Enter-key default-confirm.** Should ConfirmDialog auto-activate the
   Confirm button on Enter regardless of which footer button has focus?
   The standard ARIA dialog pattern allows this; the M1 implementation
   does not. (Leaning: not for destructive variant — too dangerous; maybe
   for `default` variant.)
2. **Async-confirm dialog persistence.** §3 documents that the dialog
   closes immediately even if `onConfirm` returns a Promise. The
   `loading` prop (Semantic §7) is the proposed fix. Confirm the
   priority for M1 fast-follow.
3. **Cancel vs other close routes.** Functionally equivalent today
   (all fire `onOpenChange(false)`). Should the contract preserve this
   equivalence or distinguish "explicit cancel" from "dismiss"?
   (Leaning: keep equivalent — distinguishing adds API surface for an
   edge-case need.)
