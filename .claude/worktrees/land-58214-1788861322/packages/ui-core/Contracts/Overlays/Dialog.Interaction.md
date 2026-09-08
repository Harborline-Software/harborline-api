# Dialog — Interaction Contract

- **Component:** Dialog
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Dialog.Semantic.md) · [Styling](./Dialog.Styling.md) · [Accessibility](./Dialog.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/dialogs/Dialog.tsx`
- **Catalog row:** #43 Dialog (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

Dialog is built on Radix UI's `@radix-ui/react-dialog` primitive. Most modal
behaviour — focus trap, ESC-to-close, click-outside-to-close, scroll lock,
return-focus-to-trigger — is inherited from Radix. This contract documents
the public behavioural surface, the regional interaction (header / body /
footer), and the explicit close gestures.

Visual styling and ARIA wiring are owned by Styling and Accessibility (PAO).

---

## 2. Open / close lifecycle

Dialog is fully controlled — the host owns `open`. The user can trigger
close gestures; the host can open and close programmatically.

### Open transition

- **Trigger:** the host sets `open = true`.
- **Behaviour:** Radix mounts the portal, renders the overlay and content,
  scroll-locks the body, traps focus inside the dialog, and moves initial
  focus to the first focusable element inside (typically the close button
  in the implementation's header).
- **Animations:** Tailwind / Radix data-state animations fade in the
  overlay and zoom/slide-in the content
  (`data-[state=open]:animate-in data-[state=open]:fade-in-0
  data-[state=open]:zoom-in-95 data-[state=open]:slide-in-from-top-[48%]`).
  Tokens for the animation timing are owned by PAO Styling.

### Close transition

Three user-initiated close gestures plus programmatic close:

1. **Close button (header `X`):** clicking the close button fires
   `<Dialog.Close>` which yields `onOpenChange(false)`.
2. **Overlay click:** clicking the overlay fires `onOpenChange(false)`
   (Radix default; not suppressed in M1).
3. **Escape key:** pressing Escape while the dialog has focus fires
   `onOpenChange(false)` (Radix default; not suppressed in M1).
4. **Programmatic close:** the host sets `open = false` directly (e.g.
   after a successful form submit).

All four routes converge on the same exit-animation path. The host's
`onOpenChange(false)` handler typically sets state and rerenders with
`open = false`; Radix runs the exit animation and unmounts the portal.

### Return-focus

When the dialog closes, focus returns to the element that had focus before
the dialog opened (Radix default). The host can override via Radix's
`onCloseAutoFocus` — not exposed in M1 (deferred).

---

## 3. Focus trap

While the dialog is open:

- Focus is trapped inside the dialog content. Tab cycles through the
  dialog's focusable descendants (header close button → body controls →
  footer buttons → wrap back to close button).
- The underlying page is unfocusable and inert (Radix handles `aria-hidden`
  / `inert` attributes on siblings).
- Focus does not escape the dialog under any user gesture except a close
  gesture.

---

## 4. Body scroll lock

- While `open === true`, the document body is scroll-locked (Radix default).
  The body cannot scroll; the dialog content can scroll internally if it
  overflows.
- The body scrollbar-gutter compensation is a Styling concern (PAO owns the
  token for the body padding adjustment that prevents content shift when
  the scrollbar disappears).

---

## 5. Header interactions

### Close button (`X`)

- **Trigger:** click, or focus + Enter / Space.
- **Behaviour:** fires `onOpenChange(false)`.
- **Visual:** `lucide-react` `X` icon, `aria-label="Close"`, focus ring on
  keyboard focus.

### Title and description

- Non-interactive. Rendered as `<Dialog.Title>` and `<Dialog.Description>`.
- They drive `aria-labelledby` and `aria-describedby` on the dialog
  content (Radix-wired).

---

## 6. Body interactions

The body region renders whatever `children` the host passes. Dialog imposes
**no** interaction constraints — forms work normally, buttons work normally,
nested controls work normally. The only behavioural overlay is the focus
trap and scroll lock.

---

## 7. Footer interactions

When `footer` is supplied, it renders below the body. Dialog imposes no
interaction constraints on the footer — typically it contains Cancel +
Confirm buttons. The buttons must call `onOpenChange(false)` themselves if
they want the dialog to close (Dialog does not auto-close on footer button
activation — only on the four close-gesture routes in §2).

---

## 8. Keyboard behaviour (Radix-derived)

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Cycles focus among the dialog's focusable descendants. Does not escape the dialog. |
| Escape | Closes the dialog — fires `onOpenChange(false)`. |
| Enter / Space (on close button) | Activates the close button — fires `onOpenChange(false)`. |
| Enter / Space (on footer buttons) | Activates the focused footer button. The button's `onClick` runs; the host typically calls `onOpenChange(false)` inside it. |

---

## 9. Interaction-state precedence

Dialog has two macro states:

1. **Closed** (`open === false`) — the dialog is unmounted; no interaction
   with the dialog is possible.
2. **Open** (`open === true`) — the dialog is mounted; focus is trapped;
   body is scroll-locked; underlying page is inert; close gestures
   (close-button click, overlay click, Escape, programmatic) are the only
   exit routes.

---

## 10. Council open questions (Interaction)

1. **[RESOLVED — M1.1]** Overlay-click suppression: `closeOnOverlayClick?: boolean` prop added to Semantic contract (Dialog.Semantic.md §3). Callers pass `closeOnOverlayClick={false}` for unsaved-state flows.
2. **[RESOLVED — M1.1]** Escape-key suppression: `closeOnEscape?: boolean` prop added to Semantic contract (Dialog.Semantic.md §3). Callers pass `closeOnEscape={false}`.
3. **[OPEN]** Initial focus override: `onOpenAutoFocus` is not yet exposed. Hosts wanting to override initial focus must use `DialogContent`'s `onOpenAutoFocus` prop via Radix passthrough. Track as RA-10 (see upf-meta-validation.md Deferred Work Register).
