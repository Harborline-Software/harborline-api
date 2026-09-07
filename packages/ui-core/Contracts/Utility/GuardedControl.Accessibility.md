# GuardedControl — Accessibility Contract

- **Component:** GuardedControl
- **ADR 0017 family:** Utility
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GuardedControl.Semantic.md) · [Interaction](./GuardedControl.Interaction.md) · [Styling](./GuardedControl.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/guards/GuardedControl.tsx`
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

The guarded switch is an accessibility-first primitive: the CIC ruling made the a11y requirements
**first-class, not deferred**. A guard that only works with a mouse, or whose state is invisible to a
screen reader, is a defect. Every requirement below is keyed to a WCAG 2.2 AA success criterion or a
WAI-ARIA 1.2 authoring practice. Baseline: `_shared/design/accessibility.md`.

---

## 2. Covered state — the SR-announced guarded name

The covered control is a native `<button type="button">`. Its accessible name folds the guarded hint
and the action label:

```
aria-label = "{chrome.guard.covered.hint} — {label}"
           = "Guarded — press to unlock — Unlock Build mode"
```

So a screen-reader user hears both **that the control is guarded** and **what it does** before
activating it. `data-guard-state="covered"` is present for testing/analytics (not an ARIA signal).

**WCAG:** SC 4.1.2 Name, Role, Value · SC 2.4.6 Headings and Labels.

---

## 3. Keyboard two-step (no hover-only cover)

| Step | Key | Result |
|---|---|---|
| Focus the covered control | Tab | Standard focus; a visible focus ring (Styling §focus). |
| Arm | Enter / Space | Cover flips open; **focus moves to the exposed FIRE control**. |
| Fire | Enter / Space | Commits (the FIRE control already has focus). |

The flip is **never hover-gated** — it is driven by activation, so it is fully operable from the
keyboard. Focus is explicitly moved to the FIRE control on arm so the second keystroke lands on the
right target (the load-bearing focus management of the two-step).

**WCAG:** SC 2.1.1 Keyboard · SC 2.4.3 Focus Order · SC 2.4.7 Focus Visible.

---

## 4. Armed state — the live, announced countdown

On arm, a **polite live region** (`role="status"`, `aria-live="polite"`, visually hidden) announces
the armed hint with the action label and the remaining seconds:

```
chrome.guard.armed.hint = "Armed — press to {action}. {seconds}s left."
                        → "Armed — press to Unlock Build mode. 5s left."
```

`{seconds}` is produced by the active `HarborlineLocaleProvider` number-format seam before catalog
interpolation, so the announced digit shape follows the active locale or host formatter.

The **visible** countdown renders in the FIRE control as a `role="timer"` element with an
`aria-label` of the locale-formatted seconds value, so assistive tech can query the remaining time on demand. The live
region announces **once on arm** (not per tick) — a per-second re-announcement would flood the SR
(the Office-2000-menu lesson applied to audio: churn is a defect). The timer role carries the
continuously-updating value for pull-based inspection.

**WCAG:** SC 4.1.3 Status Messages · SC 1.3.1 Info and Relationships.

---

## 5. Spring re-cover — announced

When the cover auto-re-covers WITHOUT a fire (timeout / navigation / blur-beyond-grace), the live
region announces `chrome.guard.recovered.announce` ("Re-guarded.") so a screen-reader user learns the
control has returned to its guarded state and their window has closed. The **post-fire** shut is
silent (the user just acted — no announcement needed).

**WCAG:** SC 4.1.3 Status Messages · SC 3.2.2 On Input (state change is a consequence of the user's own
action or an announced timeout, never a surprise).

---

## 6. Reduced motion

The cover flip is a motion affordance. When `prefers-reduced-motion: reduce` is set (detected via
`useMediaQuery('(prefers-reduced-motion: reduce)')`), the transition classes are **omitted** — the
state swaps instantly, with no flip animation. The functional two-step is unchanged; only the motion
is removed.

**WCAG:** SC 2.3.3 Animation from Interactions.

---

## 7. Disabled (accessible denial)

When `disabled` (e.g. the user lacks `workshop:unlock`), the covered control renders as a disabled
`<button aria-disabled>` — focusable-by-AT for discoverability but inert. The denial is **accessible**:
the control is present and named (so the user knows the capability exists and is gated), rather than
silently absent. The app pairs this with an explanatory affordance (the accessible `PermissionDecision`
denial copy) per the B5 mechanics.

**WCAG:** SC 4.1.2 Name, Role, Value · SC 3.3.1 Error Identification (the gate is legible, not silent).

---

## 8. Heavy-composition deferral state

A heavy composition renders a disabled `<button aria-disabled>` + a visible explanatory note
(`chrome.guard.pending.unavailable`). The note is real on-screen text (not `title`-only), so it is
available to every user, and the button's disabled state is conveyed through `aria-disabled` +
`disabled`.

**WCAG:** SC 1.3.1 Info and Relationships · SC 4.1.2 Name, Role, Value.

---

## 9. Touch targets

Both the covered and FIRE controls render at `px-3 py-2 text-sm` → effective ~36 px height, exceeding
the 24 × 24 minimum with spacing margin.

**WCAG:** SC 2.5.8 Target Size (Minimum).

---

## 10. Do / Don't

### Do
- Pass a real, localized `label` — it is spoken in both the covered name and the armed announcement.
- Use `disabled` for a permission-gated denial (accessible + legible) rather than conditionally
  removing the control (silent).
- Trust the built-in focus move on arm — do not re-manage focus in `onArm`.
- Test with keyboard only: Tab → Enter (arm) → Enter (fire); and let the countdown expire to hear
  "Re-guarded."

### Don't
- Don't wrap GuardedControl in a `title`-only tooltip to convey the guarded state — the accessible
  name already carries it.
- Don't suppress the live region — the armed announcement + re-cover announcement are the load-bearing
  SR signals.
- Don't add a hover-only reveal — the cover must be keyboard-drivable.

---

## 11. Parity notes

- **Blazor (future track):** consumes the same accessibility contract (guarded name, keyboard two-step,
  polite live region, reduced-motion, accessible denial).
- **React (this contract):** as documented.

---

## References

- ADR 0017 §A1 — Utility family contract scope
- Design note `_shared/design/first-run-and-workshop-ia-design-2026-07-06.md` §AD.2
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline + axe-core CI gate
- WAI-ARIA 1.2 — `button`, `status`, `timer`, `aria-live`, `aria-disabled`
- WCAG 2.2 SC 1.3.1 · 2.1.1 · 2.3.3 · 2.4.3 · 2.4.6 · 2.4.7 · 2.5.8 · 3.2.2 · 3.3.1 · 4.1.2 · 4.1.3
