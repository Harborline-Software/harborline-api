# Notification — Interaction Contract

- **Component:** Notification (Toast)
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Notification.Semantic.md) · [Styling](./Notification.Styling.md) · [Accessibility](./Notification.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/notifications/`
- **Catalog row:** #89 Notification / Toast (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Toast / shadcn Sonner`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

Notification's interaction surface is dominated by its **lifecycle** (enter
→ visible → exit), automatic dismiss, and dedupe-by-id semantics. This
contract documents:

- The full lifecycle of a single toast (§2).
- Auto-dismiss timing and pause-on-hover behaviour (§3).
- Manual dismiss (close button, swipe, ESC) (§4).
- Action button activation + post-action dismiss (§5).
- Dedupe + max-visible eviction (§6).
- Keyboard behaviour (§7).

---

## 2. Toast lifecycle

A single toast moves through three phases:

```
[create] → entering → visible → exiting → [dismissed]
```

### 2.1 Create

- Host code calls `notify(...)` or one of the variant convenience methods.
- The provider:
  1. Generates an `id` (if not supplied), checks for a dedupe collision.
  2. Pushes the toast into the active set.
  3. Schedules the auto-dismiss timer (if `duration > 0`).
  4. Triggers the enter animation.

### 2.2 Entering

- The toast slides / fades into its corner-pinned position (animation
  owned by PAO Styling; canonical: slide-up + fade-in over ~250ms).
- The toast is fully interactive during the enter animation — clicks on
  the close button or action button work immediately.

### 2.3 Visible

- The toast displays its title / description / action.
- The auto-dismiss timer counts down.
- Pause-on-hover (§3.2) may suspend the timer.

### 2.4 Exiting

- The toast slides / fades out (~200ms).
- The auto-dismiss timer (if any) is cancelled.
- During exit, the toast does not respond to clicks (Sonner-canonical
  behaviour — late clicks on a fading toast are confusing).

### 2.5 Dismissed

- The toast is removed from the active set.
- `onDismiss` fires.
- The viewport layout reflows other toasts to fill the gap.

---

## 3. Auto-dismiss timing

### 3.1 Timer

- Timer starts when the toast enters the visible phase.
- Duration is per-toast (`options.duration`) or provider's
  `defaultDuration` (4000ms canonical).
- `duration: 0` skips the timer entirely — the toast is persistent until
  manually or programmatically dismissed.
- When the timer fires, the toast transitions to exiting.

### 3.2 Pause on hover / focus

- When the user hovers (pointer-enter) or focuses (keyboard focus) any
  active toast, the auto-dismiss timer for that toast is **paused**.
- When the user pointer-leaves or blurs, the timer resumes from where
  it paused.
- This prevents the toast from disappearing while the user is reading
  or interacting with it.

### 3.3 Pause on viewport-blur

When the browser tab loses focus (e.g. user switches to another tab),
timers across the entire provider are **paused** (Sonner-canonical). When
the tab regains focus, timers resume. Council open question 2 considers
whether to also reset the timer on tab-regain.

---

## 4. Manual dismiss

A toast can be dismissed manually via:

### 4.1 Close button

- Each toast renders a small "x" close button (typically in the top-
  right corner).
- Clicking the close button transitions the toast to exiting.

### 4.2 Swipe-to-dismiss (council open question 1)

- Pointer-drag horizontally past a threshold dismisses the toast.
- This is the Sonner / iOS-canonical pattern. Council confirms whether
  M2 ships with swipe.

### 4.3 Programmatic dismiss

- `notify.dismiss(id)` triggers exit for a specific toast.
- `notify.dismiss()` (no arg) triggers exit for all active toasts.

### 4.4 ESC

- When focus is inside the notification viewport (e.g. the user tabbed
  into a toast), pressing ESC dismisses the focused toast (council open
  question 3 — alternative is "ESC dismisses all").

---

## 5. Action button

When `options.action` is supplied, the toast renders the action label
inline:

- **Activation:** click, or Enter/Space when the action button has focus.
- **Callback:** fires `action.onClick()`.
- **Post-action behaviour:** the toast then transitions to exiting (per
  Semantic open question 2 — this spec auto-dismisses; council may
  prefer to keep the toast visible).
- **`onDismiss` order:** fires after `action.onClick` in the same tick.

---

## 6. Dedupe + max-visible eviction

### 6.1 Dedupe by `id`

When `notify({ id: 'foo', ... })` is called and a toast with `id: 'foo'`
is already active:

- The existing toast's content is **updated** in-place with the new
  options.
- The existing toast's auto-dismiss timer is **reset** to the new
  `duration` (council open question 4 — alternative is to preserve the
  existing timer).
- The existing toast does **not** fire `onDismiss` — it's the same
  toast, just updated.
- Animation: the toast may briefly flash / pulse to signal the update
  (PAO Styling owns whether this animation is present).

### 6.2 Max-visible eviction

When a new toast would exceed `provider.max`:

- The **oldest** active toast (longest time since enter) is selected for
  eviction.
- The evicted toast transitions to exiting; its `onDismiss` fires with
  no special discriminator (council open question 5 — should `onDismiss`
  receive a "reason" payload like `'auto' | 'manual' | 'evicted' |
  'replaced'`?).
- The new toast enters once the evicted toast has exited.

### 6.3 Dedupe + max-visible interaction

A dedupe-update does NOT count as a new toast for the max-visible
threshold — the active count stays the same. The provider's max applies
to distinct active `id`s.

---

## 7. Keyboard behaviour

The notification viewport is a **secondary focus zone** — the user tabs
into it intentionally to interact with a toast, then tabs out.

| Key | Context | Behaviour |
| --- | --- | --- |
| Tab | Outside viewport | Standard. Pressing Tab past the last main-content focusable lands in the notification viewport (PAO Accessibility decides exact tab order). |
| Tab | Inside viewport | Moves focus between the action button, close button of the focused toast, and the next/previous toast's controls. |
| Shift+Tab | Inside viewport | Reverse. |
| Enter / Space | On action / close button | Activates the button. |
| ESC | Inside viewport | Dismisses the focused toast (council open question 3). |

The viewport itself is a `role="region"` with `aria-live="polite"` (PAO
Accessibility owns the exact ARIA wiring); new toasts announce on enter.

---

## 8. Focus behaviour

### 8.1 New toast on enter

- New toasts do **not** auto-focus on enter. Focus stays wherever the
  user put it (typically still in the main content where they triggered
  the action).
- This is the right default for non-critical toasts — auto-focusing
  would steal focus and disrupt the user's flow.

### 8.2 `danger` variant focus behaviour (open question)

- Council open question 6: should `danger` toasts auto-focus to ensure
  the user notices? Leaning no — even errors shouldn't steal focus;
  but PAO Accessibility may have a different view.

### 8.3 Focus when a focused toast is dismissed

- If a toast has keyboard focus when it dismisses (auto-timer, manual,
  evicted), focus moves to the next toast in the viewport (if any), or
  back to the element that had focus before the user tabbed into the
  viewport (canonical "focus-return" pattern).

---

## 9. Interaction-state precedence

When multiple state conditions apply to a toast, resolve in this order
(highest precedence first):

1. **Exiting phase** — toast is non-interactive (clicks suppressed);
   only the exit animation runs.
2. **Paused (hovered/focused)** — visible phase; timer suspended.
3. **Visible** — normal interactive state.
4. **Entering phase** — fully interactive but mid-animation (clicks
   work; visual state mid-transition).

Action click and close click are equally valid in entering, visible, and
paused phases; both abort the auto-dismiss timer immediately and trigger
exit.

---

## 10. Council open questions (Interaction)

1. **Swipe-to-dismiss in M2.** Sonner ships swipe; some teams find it
   accidental (especially on touchpads). Leaning include — it's the
   pattern users expect; we can add an opt-out provider prop if
   accidental dismissals prove problematic.
2. **Tab-regain timer reset.** When the browser tab regains focus,
   should timers reset (allowing a fresh `duration` to read missed
   toasts), or resume from pause (this spec)? Leaning resume — reset
   feels like cheating; the user can hover to pause.
3. **ESC dismiss-focused vs dismiss-all.** This spec dismisses the
   focused toast. Some teams prefer ESC = dismiss all. Leaning
   focused-only — granular control.
4. **Dedupe timer reset behaviour.** This spec resets the timer on
   dedupe-update. Alternative: preserve existing timer (so a quick
   sequence of `notify({ id: 'x' })` calls doesn't keep extending
   the toast's life). Leaning reset — most dedupe use cases want the
   updated message to have a full reading time.
5. **`onDismiss` reason payload.** Should `onDismiss(reason: 'auto' |
   'manual' | 'action' | 'evicted' | 'replaced' | 'programmatic')`
   replace the no-arg signature? Leaning yes — hosts often want to
   know "did the user act on it or did it just disappear?". Add in
   M2.
6. **Danger-toast auto-focus.** Whether `danger` variant toasts should
   steal focus. Leaning no — the `aria-live="polite"` already
   announces the toast; stealing focus disrupts the user.
7. **Pause-on-touch behaviour.** On touch devices, there is no
   "hover". Should touch-on-toast pause the timer (matching mouse
   hover semantics)? Sonner does this. Leaning yes — same intent,
   touch = "I'm reading this".
8. **Action click during exit phase.** What if the user clicks the
   action button during the exit animation? Today: suppressed (§2.4).
   Alternative: allow the click, abort the exit, reset to visible.
   Leaning suppress — once exit starts, it commits.
