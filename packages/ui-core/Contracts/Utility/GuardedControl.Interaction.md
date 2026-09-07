# GuardedControl — Interaction Contract

- **Component:** GuardedControl
- **ADR 0017 family:** Utility
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GuardedControl.Semantic.md) · [Styling](./GuardedControl.Styling.md) · [Accessibility](./GuardedControl.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/guards/GuardedControl.tsx`
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract documents the two-step arm/fire behaviour, the spring-loaded auto-re-cover, and the
countdown — the behaviours that make GuardedControl an aircraft guarded switch rather than a button or
a dialog. Visual tokens are owned by Styling; ARIA/SR wiring by Accessibility.

---

## 2. The state machine

```
        activate (arm)                 activate (fire)
covered ───────────────▶ armed ───────────────────────▶ committing
   ▲                       │                                 │
   │  spring re-cover      │  timeout / navigation /         │ onCommit resolves
   │  (announced)          │  window-blur>grace /            │ (spring-shut, silent)
   └───────────────────────┴─────────────────────────────────┘
```

- **covered → armed:** the covered control is activated (click, or Enter/Space when focused). Fires
  `onArm`. Does **not** fire `onCommit`. The cover flips open, the exposed FIRE control is rendered,
  and **focus moves to it** (the keyboard two-step).
- **armed → committing:** the exposed FIRE control is activated. Fires `onCommit` (awaited).
- **committing → covered:** `onCommit` resolves; the cover spring-shuts **silently** (no re-cover
  announcement — the user just acted).
- **armed → covered (spring re-cover):** the cover auto-re-covers on any of the four triggers below,
  fires `onRecover`, and **announces** `chrome.guard.recovered.announce` ("Re-guarded."). Focus is not
  forcibly moved (the exposed control unmounts; focus falls back per the browser).

---

## 3. Arm (the first conscious act)

- **Trigger:** click on the covered control, or focus + Enter / Space.
- **Behaviour:** transitions to `armed`; fires `onArm`; clears any prior announcement; the arm
  countdown starts at `composition.cover.armTimeoutMs` (default 5000 ms).
- **A stray click cannot fire.** Because arm only *exposes* the FIRE control (it does not commit), a
  single accidental activation never triggers `onCommit`. This is the whole point of the guard.
- **Disabled:** when `disabled`, activation is inert — no arm, no state change.

---

## 4. Fire (the second conscious act)

- **Trigger:** click on the exposed FIRE control, or Enter / Space (focus is already on it after arm).
- **Behaviour:** transitions to `committing`; fires `onCommit` (awaited); on resolution the cover
  spring-shuts silently.
- **Two distinct interactions.** Arm and fire are separate activations on separate controls — there is
  no single gesture that both arms and fires, and no "hold to confirm". This defeats dialog-style
  click-through habituation.

---

## 5. Spring-loaded auto-re-cover

While `armed`, the cover re-covers (→ `covered`, `onRecover`, announced) on ANY of:

| Trigger | Mechanism |
|---|---|
| **Countdown expiry** | An interval polls the deadline (`armTimeoutMs` after arm); at expiry it re-covers. |
| **Navigation** | `popstate` (SPA route change / back-forward) re-covers. |
| **Tab hidden** | `document.visibilitychange` → `hidden` re-covers. |
| **Window blur beyond grace** | `window.blur` starts an 800 ms grace timer; if focus does not return, it re-covers. `window.focus` cancels the pending grace. |
| **Unmount** | The effect cleanup clears all timers/listeners (no re-cover announcement — the component is gone). |

The cover **never lingers armed**. All timers and listeners are registered only while `armed` and torn
down on transition/unmount (no leaks).

---

## 6. Countdown

While `armed`, a **visible countdown** (whole seconds remaining) renders in the FIRE control as a
`role="timer"` badge and updates ~4×/second (250 ms poll). The integer is computed as
`Math.ceil(remaining/1000)`, routed through the active locale number-format seam, then displayed with
the contracted `s` suffix.
The **armed announcement** (§Accessibility) carries the initial seconds. The countdown is a
first-class signal, not decoration — it tells the user how long they have to fire before the cover
springs shut.

---

## 7. Keyboard behaviour

| Key | Context | Behaviour |
| --- | --- | --- |
| Enter / Space | covered control focused | Arms (fires `onArm`); focus moves to the exposed FIRE control. |
| Enter / Space | FIRE control focused | Fires (`onCommit`). |
| Tab / Shift+Tab | either state | Standard focus order; the single rendered control participates normally. |
| — | (no Escape handler) | There is no Escape-to-cover in M1; the spring timers + navigation cover it. A future amendment may add Escape → immediate re-cover. |

**No hover-only cover.** The flip is driven by activation (click / Enter / Space), never by hover — the
cover is fully keyboard-drivable (Accessibility §keyboard).

---

## 8. Controlled vs uncontrolled

- **Uncontrolled (default):** the component owns `GuardState`. `onStateChange` observes transitions.
- **Controlled:** pass `state`; the component renders that state and calls `onStateChange` on intent.
  A controlled consumer is responsible for advancing the state (e.g. to reflect a server round-trip);
  the spring timers still fire `onRecover`/`onStateChange` so the consumer can react.

---

## 9. Heavy-composition path

When `requiresPendingFlow(composition)` is true (and no `cover`), the control renders **inert**
(disabled) with the deferral note — there is no arm/fire interaction until the B10 governed pending
flow lands. See Semantic §3.3.

---

## 10. Council open questions (Interaction)

1. **Escape → immediate re-cover.** Should Escape while armed re-cover at once (a fast "never mind")?
   (Leaning: yes, as a fast-follow — it is a natural expectation and cheap.)
2. **Double-activation debounce.** Arm then an immediate second Enter fires — is that too fast to be
   "two conscious acts"? (Leaning: the focus-move + the countdown badge make the second act
   deliberate; revisit if dogfood shows accidental fires.)
