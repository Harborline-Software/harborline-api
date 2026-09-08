# Notification — Accessibility Contract

- **Component:** Notification (Toast)
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Notification.Semantic.md) · [Interaction](./Notification.Interaction.md) · [Styling](./Notification.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Sonner / Radix Toast_
- **Catalog row:** #89 Notification / Toast (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Toast / shadcn Sonner`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Notifications carry transient system feedback. Their accessibility contract
is one of the most failure-prone in any design system: the wrong ARIA live-
region role results in either silence (SR misses the message) or
disruption (SR interrupts whatever the user was doing). Auto-dismiss adds
a timer dimension that interacts with the live-region cadence.

This contract pins:

1. The `role="status"` vs. `role="alert"` choice per intent.
2. The `aria-live` cadence (polite vs. assertive).
3. The auto-dismiss timing requirement for non-error intents (WCAG 2.2.1
   Timing Adjustable interaction).
4. The dismiss-button accessibility and the swipe-to-dismiss alternative.
5. The keyboard pathway for accessing the notification.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Root role per intent

Notifications use TWO distinct roles depending on intent urgency:

| Intent | Role | `aria-live` | Rationale |
|---|---|---|---|
| `success` | `status` | implicit `polite` (don't set explicitly) | Non-urgent; SR announces when it finishes current speech |
| `info` | `status` | implicit `polite` | Non-urgent informational |
| `warning` | `status` | implicit `polite` | Non-urgent caution; user attention requested but not demanded |
| `error` | `alert` | implicit `assertive` (don't set explicitly) | Urgent; SR interrupts to announce immediately |

**Why implicit, not explicit.** `role="status"` carries implicit
`aria-live="polite"`; `role="alert"` carries implicit `aria-live=
"assertive"`. Adding the explicit attribute creates a "double live region"
that some SRs handle inconsistently — the implicit value is the canonical
pattern.

**Forbidden patterns:**

- `role="alert"` on success / info — over-interrupts the user; weakens the
  signal of real errors.
- `role="status"` on error — under-emphasises; SR may delay announcement
  long enough for the user to lose context.
- Both `role` and explicit `aria-live` together — double-source-of-truth.

**WCAG citations:**
- SC 4.1.2 Name, Role, Value.
- SC 4.1.3 Status Messages — programmatically determinable status.

---

## 3. Accessible name

The notification's accessible name is its message text. For SR-only
prefacing (e.g., "Error: ..."), include the intent verbally in the message
or rely on the icon's accessible name (when icon is visible).

| Pattern | Implementation |
|---|---|
| Icon + message | Icon is `aria-hidden="true"`; message text is the accessible name |
| Title + message | The title MAY be a `<p class="font-medium">`; SR reads title then message in DOM order (the parent `role="status"` / `role="alert"` reads the entire region) |
| Action button inside notification | Inside the live region; SR reads the action label after the message |

**Action-button gotcha.** When the notification contains an action button
("Undo", "Retry"), the live-region announcement includes the button label.
This is usually desirable ("File deleted. Undo.") but may double-read on
some SRs (the button is announced again when focused). Test in practice.

**WCAG citation:** SC 4.1.2 Name, Role, Value.

---

## 4. Auto-dismiss timing — WCAG 2.2.1 Timing Adjustable

Notifications that auto-dismiss enforce a timing constraint on the user.
WCAG 2.2.1 Timing Adjustable applies — with exceptions:

- **Exception (Required):** The timing is essential to the activity (e.g.,
  a flight booking countdown). NOT this case.
- **Exception (Real-time):** Real-time events (e.g., live chat). NOT this
  case.
- **Exception (Essential):** Invalidating the time would invalidate the
  activity. NOT this case.
- **Exception (20 hours):** Timing is more than 20 hours. NOT this case.

Therefore, notifications subject to auto-dismiss MUST provide ONE of:

a. **Turn off.** User can disable the auto-dismiss (preference + per-
   notification).
b. **Adjust.** User can extend the auto-dismiss duration to ≥ 10× the
   default.
c. **Extend.** User receives a warning before timeout with ≥ 20s to
   extend; can extend with a simple action.

**Implementation pattern (b).** Hover or focus on the notification PAUSES
the auto-dismiss timer (the user has indicated they're reading; reset the
clock). Pointer-leave + blur RESUME the timer.

The Interaction contract owns the timer behaviour; this contract names
the requirement.

**ERROR intent.** Error notifications MUST NOT auto-dismiss at all.
They persist until the user dismisses.

**WCAG citation:** WCAG 2.2 SC 2.2.1 Timing Adjustable.

---

## 5. Dismiss affordances

Three pathways:

| Pathway | Implementation | Requirement |
|---|---|---|
| Dismiss button | Native `<button aria-label="Dismiss notification">` with X icon | REQUIRED on persistent (error) notifications; OPTIONAL on auto-dismiss; touch target ≥ 24×24 per WCAG 2.5.8 |
| Escape key | Press Escape with focus inside the notification | RECOMMENDED |
| Swipe gesture | Touch swipe horizontally on mobile | OPTIONAL; pointer-only motion (alternative MUST exist — see WCAG 2.5.7) |

**WCAG citation:** WCAG 2.2 SC 2.5.7 Dragging Movements — if swipe-to-
dismiss is supported, the dismiss button MUST also exist as the single-
pointer alternative. Swipe MUST NOT be the only dismiss path.

**Forbidden patterns:**

- Toast that has NO dismiss button AND auto-dismisses — user can't
  acknowledge / extend; if SR misses the announcement, the user is left
  unaware.
- Toast that requires hovering the dismiss button (no keyboard reach) to
  reveal it.

---

## 6. Keyboard

Notifications are non-focusable by default. The action button and dismiss
button inside are focusable. Reaching the notification:

| Key | Behaviour |
|---|---|
| Tab | Focus traverses page content in DOM order. Notifications appear in a separate fixed-position region at the END of `<body>`; depending on DOM order, Tab MAY OR MAY NOT reach them |
| Tab from inside the toaster region | Moves to the next focusable inside the notification (action → dismiss) and then to the next focusable on the page |
| Escape | (when focus is inside a notification) Dismisses that notification |
| F6 / Ctrl+F6 | Browser-/AT-specific commands to jump between page regions. Some screen readers expose the toaster region as a navigation target |

**Recommended pattern.** Implementations SHOULD provide a keyboard
shortcut to focus the most recent notification (e.g., Alt+T). The
shortcut is OUT OF M2 SCOPE; the contract names the gap.

**WCAG citation:** SC 2.1.1 Keyboard.

---

## 7. Focus management

| Event | Required behaviour |
|---|---|
| Notification appears | Focus does NOT move to the notification (context change) |
| Notification dismissed | Focus does NOT shift; stays wherever it was (typically on the action button that triggered the notification) |
| User focuses dismiss button + presses Enter | Notification dismisses; focus stays on the now-removed button position (browser default falls back to body) |
| User focuses action button + presses Enter | Action fires; notification SHOULD remain visible briefly OR dismiss + return focus to the action-trigger location (per design intent) |

**The "stays put" rule** is important. Auto-focusing a notification or
its action button on appear violates WCAG SC 3.2.1 On Focus (unexpected
focus motion). The live region announces; the user decides whether to
engage.

**WCAG citations:**
- SC 3.2.1 On Focus.
- SC 3.2.5 Change on Request (the dismiss is user-initiated).

---

## 8. Color contrast

Per [Notification.Styling §2](./Notification.Styling.md) and
`feedback.tokens.json` (proposed addition) defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Message text on notification background (every intent) | 4.5:1 | SC 1.4.3 |
| Left-stripe border on notification background | 3:1 | SC 1.4.11 |
| Dismiss-button glyph on notification background | 3:1 (non-text-contrast for the glyph) | SC 1.4.11 |
| Action button (inline link styling) on notification background | 4.5:1 | SC 1.4.3 |
| Focus ring on dismiss / action buttons | 3:1 against both surfaces | SC 1.4.11 / SC 2.4.13 |

The default token values MUST be pre-verified. Provider overrides MUST
re-verify.

**Color is not the only channel.** Intent carries via THREE channels:
intent icon glyph (visual shape), background tint (visual colour), and
the implicit `role="status"` / `role="alert"` (SR channel).

**WCAG citation:** SC 1.4.1 Use of Color.

---

## 9. Touch target

Dismiss button MUST present ≥ 24×24 CSS pixels. Action button MUST
present ≥ 24×24 (typically a text link — verify per locale).

**Mobile swipe-to-dismiss.** When implemented, the swipe gesture MUST
have a single-pointer alternative (the dismiss button per §5).

**WCAG citations:** SC 2.5.7 Dragging Movements; SC 2.5.8 Target Size
(Minimum).

---

## 10. Reduced motion

Entry + exit animations MUST honor `prefers-reduced-motion: reduce` (see
Styling §6).

**WCAG citation:** SC 2.3.3 Animation from Interactions.

---

## 11. Toaster region

The toaster region is the container that holds the active stack:

| Element | Role | Notes |
|---|---|---|
| Toaster region container | `region` (via `role="region"`) | OPT-IN; some implementations use a plain `<div>` with no role to avoid landmark inflation |
| `aria-label` on the region | "Notifications" (i18n-localised) | REQUIRED if `role="region"` is set |

**Open question.** Whether the toaster region carries `role="region"`
landmark status. Pro: SR users can jump to it via landmark navigation.
Con: another landmark adds noise. Default: NO `role="region"` —
notifications are transient and the live-region announcements are the
primary access channel.

---

## 12. Do / Don't

### Do

- Use `role="status"` for success / info / warning intents (implicit
  polite live region).
- Use `role="alert"` ONLY for error intent (implicit assertive live
  region).
- Pause auto-dismiss on hover / focus.
- Persist error notifications until user dismisses.
- Provide a dismiss button (always reachable by keyboard + ≥ 24×24).
- Include the intent icon as a cross-channel signal beyond colour.

### Don't

- Don't use `role="alert"` for non-error intents. It over-interrupts.
- Don't auto-dismiss error notifications.
- Don't auto-focus the notification or its buttons on appear.
- Don't make swipe-to-dismiss the ONLY dismiss path. Mouse + keyboard
  paths are mandatory.
- Don't ship notifications on top of dialogs. Dialogs own user
  attention; notifications create focus thrash.
- Don't put critical-action-only paths inside notifications.

---

## 13. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Sonner OR Radix Toast.
- **Web Components (Phase M4, Lit):** Vaadin Notification is a candidate.

---

## 14. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | `role="status"` is emitted for success/info/warning intents | Snapshot test |
| F2 | `role="alert"` is emitted for error intent | Snapshot test |
| F3 | Implicit live region announces in target SRs (NVDA, JAWS, VoiceOver) | Manual SR test |
| F4 | Auto-dismiss pauses on hover / focus | E2E test |
| F5 | Error notifications NEVER auto-dismiss | E2E test |
| F6 | Dismiss button has accessible name + 24×24 target | Snapshot + manual measurement |
| F7 | Swipe-to-dismiss (if implemented) has dismiss-button fallback | Manual test |
| F8 | `prefers-reduced-motion` suppresses entrance + exit animations | Manual test with reduce-motion on |
| F9 | Focus does NOT auto-move to the notification on appear | E2E test |
| F10 | Contrast verification for all four intents | Run axe scan |
| F11 | Action button inside notification is keyboard-reachable | E2E Tab test |
| F12 | More than 5 simultaneous notifications collapse (FIFO or "+ N more") | E2E stress test |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #89 Notification / Toast (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Sonner — primary foundation
- Radix Toast — alternative foundation
- WAI-ARIA Authoring Practices Guide — Alert / Status patterns
- [Notification.Semantic.md](./Notification.Semantic.md) — prop contract
- [Notification.Interaction.md](./Notification.Interaction.md) — timing, swipe-to-dismiss
- [Notification.Styling.md](./Notification.Styling.md) — token surface + visual states
- [StatusBanner.Accessibility.md](./StatusBanner.Accessibility.md) — companion (persistent in-flow message)
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `status`, `alert`, `aria-live`, `aria-atomic`
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.2.1 Timing Adjustable
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.7 Dragging Movements
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 3.2.5 Change on Request
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
