# Drawer — Interaction Contract

- **Component:** Drawer (Sheet)
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Drawer.Semantic.md) · [Styling](./Drawer.Styling.md) · [Accessibility](./Drawer.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/dialogs/Drawer.tsx`
- **Catalog row:** #46 Drawer (`app-priority: high`, `library-scope: v1`, `Notes: alias: Sheet (shadcn)`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Related contract:** [Dialog.Interaction.md](./Dialog.Interaction.md) — Drawer's interaction
  surface mirrors Dialog's; this contract documents the differences (edge-slide animation, modal vs
  non-modal mode).

---

## 1. Scope

Drawer wraps Radix UI's Dialog primitive with edge-slide animation and an
optional non-modal mode. Most interaction behaviour mirrors Dialog
(`Dialog.Interaction.md`); this contract documents:

- The open/close lifecycle (§2).
- The modal vs non-modal interaction difference (§3).
- Focus trap behaviour in modal mode (§4).
- Keyboard handling (§5).
- Slide-animation interaction (§6).

---

## 2. Open / close lifecycle

### 2.1 Opening

The host sets `open: true`. Radix mounts the drawer portal and triggers
the enter animation:

1. Portal mounts at document body.
2. Scrim (modal mode only) fades in.
3. Drawer panel slides in from `side` edge over ~300ms.
4. Focus moves into the drawer (modal mode: first focusable; non-modal:
   focus stays where the user put it — council open question 1).

### 2.2 Closing

A close request can come from:

- **Close button** (top-right X) — click or Enter/Space when focused.
- **Scrim click** (modal mode only) — Radix default.
- **ESC press** — when the drawer has focus (modal mode) or is open
  (non-modal mode, per current spec; Semantic open question 2).
- **Programmatic** — host sets `open: false`.

In every case:

1. `onOpenChange(false)` fires.
2. Host updates `open` to `false`.
3. Drawer panel slides out toward `side` edge over ~250ms.
4. Scrim (if modal) fades out concurrently.
5. Radix unmounts the portal.
6. Focus returns to the element that opened the drawer (modal mode
   only; non-modal mode preserves whatever focus was at).

---

## 3. Modal vs non-modal interaction

### 3.1 Modal mode (`modal: true`, default)

- **Scrim:** captures pointer events; clicking the scrim fires
  `onOpenChange(false)`.
- **Page interaction:** suspended — pointer events do not reach the
  page underneath.
- **Body scroll:** locked (Radix default; PAO Styling owns the
  scrollbar-gutter compensation).
- **Focus trap:** active — Tab cycles within the drawer; Shift+Tab
  cycles backwards. Focus does not leak to the page.
- **ESC:** dismisses the drawer.

### 3.2 Non-modal mode (`modal: false`)

- **No scrim** — page underneath is fully visible and interactive.
- **Page interaction:** normal — clicks land on whichever element is
  underneath the pointer.
- **Body scroll:** unlocked — page scrolls as usual.
- **No focus trap** — Tab moves between drawer content and page
  content. Tab order follows DOM order, with the drawer portal
  inserted at its render-time location.
- **ESC:** still dismisses the drawer in the current spec (Semantic
  open question 2 considers).
- **Close button:** still present and dismisses.

### 3.3 Mode change mid-session

If the host changes `modal` while the drawer is open (e.g.
desktop-to-mobile resize), the drawer re-renders with the new mode:

- Modal → non-modal: scrim fades out; focus trap releases; body scroll
  unlocks. Currently-focused element stays focused.
- Non-modal → modal: scrim fades in; focus trap engages (moving focus
  to the first focusable inside if focus was outside); body scroll
  locks.

Hosts should generally avoid mid-session mode changes for predictability.

---

## 4. Focus trap (modal mode)

The Radix Dialog primitive's focus trap applies:

- On open: focus moves to the first focusable element inside the
  drawer (typically the close button, or the first form input).
- Tab cycles within the drawer; Shift+Tab cycles backwards. Focus
  cannot escape to the page.
- On close: focus returns to the element that had focus before the
  drawer opened (the activating button, typically).

### 4.1 Manual focus override

The host can override the initial focus target by wrapping their own
focus-management hook around the drawer; the contract does not expose
an `initialFocusRef` prop in M2 (deferred per Semantic §7).

### 4.2 Focus inside non-modal drawers

In non-modal mode, focus moves into the drawer on open (per Radix
Dialog default). Tab continues out of the drawer into the page. This is
mostly the right behaviour — the user opens the drawer to interact with
it — but council open question 1 considers whether non-modal drawers
should leave focus alone on open.

---

## 5. Keyboard behaviour

| Key | Context | Behaviour |
| --- | --- | --- |
| Tab / Shift+Tab | Modal drawer open | Cycles within drawer (focus trap). |
| Tab / Shift+Tab | Non-modal drawer open | Moves between drawer + page normally. |
| ESC | Drawer open (either mode) | Fires `onOpenChange(false)`. |
| Enter / Space | On close button | Fires `onOpenChange(false)`. |
| Enter / Space | On footer actions | Fires the respective button's `onClick`. |
| ArrowKeys | Inside drawer body | No drawer-level binding; descendants handle. |

---

## 6. Slide animation interaction

The drawer's slide-in / slide-out animation is **300ms enter / 250ms
exit** (canonical; PAO Styling owns exact timing + easing). During the
animation:

- **Enter:** the drawer is interactive from the moment it begins
  sliding in (clicks on the partially-visible panel work). Focus
  moves into the drawer when the enter animation completes (Radix
  default — focus doesn't move until the focus target is in place).
- **Exit:** when `onOpenChange(false)` fires, the drawer immediately
  loses interactivity (clicks on the still-visible panel are
  suppressed — sliding-out drawer interactions are confusing). The
  panel completes the slide-out, then unmounts.

### 6.1 Rapid open/close

If the user dismisses a drawer that just opened (or programmatic code
toggles `open` rapidly), Radix handles the race naturally — the
animation reverses direction smoothly. No contract-level handling
required.

### 6.2 `prefers-reduced-motion`

When the user has `prefers-reduced-motion: reduce` set, the slide
animation collapses to an instant appear / disappear (PAO Styling owns
the media-query gate). Behaviour is otherwise identical.

---

## 7. Scroll behaviour

### 7.1 Inside the drawer

Drawer body content can overflow vertically; the body region has
`overflow-y: auto` so internal scrolling is supported. The header and
footer remain pinned at top / bottom of the drawer.

For `side='top'` / `'bottom'` drawers, content overflow scrolls in the
height dimension; for `side='left'` / `'right'`, it scrolls vertically
inside the (fixed-width) drawer.

### 7.2 Page scroll (modal mode)

Body scroll is locked while a modal drawer is open. The drawer panel
itself owns the scroll.

### 7.3 Page scroll (non-modal mode)

Body scroll is unlocked; the page scrolls normally. The drawer remains
at its fixed `side` position regardless of page scroll.

---

## 8. Interaction-state precedence

When multiple state conditions apply, resolve in this order (highest
precedence first):

1. **Exiting animation** — drawer non-interactive; only the exit
   animation runs.
2. **`open === true` + modal** — focus trapped; scrim blocks page.
3. **`open === true` + non-modal** — drawer interactive; page also
   interactive.
4. **Entering animation** — drawer interactive from animation start;
   focus moves on animation completion.
5. **`open === false`** — drawer unmounted (portal absent).

---

## 9. Council open questions (Interaction)

1. **Initial focus in non-modal mode.** Today: focus moves into the
   drawer on open (Radix default). Some teams want focus to stay where
   the user put it for non-modal drawers (so they can keep typing in
   the underlying form while a context drawer opens beside them).
   Leaning **stay** for non-modal — defer to PAO Accessibility.
2. **ESC in non-modal mode.** Today: ESC dismisses. Alternative: ESC
   bubbles to host's global handlers (some apps want ESC to also
   dismiss menus / clear selection / etc.). Leaning ESC still
   dismisses, with `closeOnEscape: false` opt-out (Semantic open
   question 4).
3. **Scrim-click dismiss.** Today: scrim-click dismisses (Radix
   default). Aligned with Dialog precedent. The `closeOnBackdropClick:
   false` opt-out (Semantic open question 4) addresses the lose-edit
   footgun.
4. **Focus return on close (non-modal).** Today: focus stays wherever
   the user put it. Should non-modal close also return focus to the
   opener? Leaning **stay** — non-modal drawers don't expect to
   capture+release focus.
5. **Touch swipe-to-close.** On touch devices, a swipe toward the
   `side` edge could dismiss the drawer. Sonner/Vaul-style behaviour.
   Defer to a future enhancement.
6. **Drag handle on `side='bottom'` mobile drawers.** Common pattern:
   a small horizontal "grabber" line at the top of bottom-slide
   drawers for visual hinting + drag-to-dismiss. Out of scope for
   M2 baseline; PAO Styling may add visual treatment.
7. **Stacking — z-index ordering.** If two drawers open simultaneously
   (deferred per Semantic §7), how do they stack? Out of scope for
   M2; council ratifies the stack policy when the feature lands.
