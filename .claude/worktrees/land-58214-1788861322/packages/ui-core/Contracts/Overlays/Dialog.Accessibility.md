# Dialog — Accessibility Contract

- **Component:** Dialog
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Dialog.Semantic.md) · [Interaction](./Dialog.Interaction.md) · [Styling](./Dialog.Styling.md)
- **Related contract:** [ConfirmDialog.Accessibility.md](./ConfirmDialog.Accessibility.md) — ConfirmDialog inherits this contract verbatim and adds three confirmation-specific concerns.
- **Reference implementation:** `packages/ui-react/src/components/dialogs/Dialog.tsx` (built on `@radix-ui/react-dialog`)
- **Catalog row:** #43 Dialog (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

Dialog is the most-accessibility-load-bearing component in Batch C. A
modal overlay has six mandatory WAI-ARIA / WCAG concerns that all MUST be
satisfied for the component to be usable by keyboard and AT users:

1. **`aria-modal="true"`** on the content panel.
2. **`aria-labelledby` + `aria-describedby`** wiring title and description
   to the panel.
3. **Focus trap** — Tab and Shift+Tab cycle focus inside the dialog only.
4. **Initial focus** — focus moves into the dialog on open.
5. **Return focus on close** — focus returns to the element that triggered
   the open.
6. **Escape-to-close** — Escape closes the dialog.

Radix's `@radix-ui/react-dialog` implements all six. This contract names
the Radix-inherited behaviour, the SelectField-style additions on top
(`aria-label="Close"` on the close button), and one documented gap
(reduced-motion class addition).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-
ARIA 1.2 authoring practice.

---

## 2. ARIA structural roles

| Element | Radix-emitted role | Confirms in M1 |
|---|---|---|
| `<DialogPrimitive.Overlay>` | `<div>` (no role) | yes |
| `<DialogPrimitive.Content>` | `role="dialog"` + `aria-modal="true"` + `aria-labelledby` + `aria-describedby` | **partial** — `aria-modal="true"` is set EXPLICITLY in M1 (see §3); `aria-labelledby` / `aria-describedby` are auto-wired by Radix |
| `<DialogPrimitive.Title>` | implicit `<h2>` (via `<DialogPrimitive.Title>`'s default rendering as a heading) | yes |
| `<DialogPrimitive.Description>` | `<p>` with a Radix-generated id consumed by the content's `aria-describedby` | yes |
| `<DialogPrimitive.Close>` | native `<button>` with `aria-label="Close"` | yes |

**WAI-ARIA Authoring Practices** — Dialog (Modal) pattern:
<https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/>

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. `aria-modal="true"` emission

The M1 implementation EXPLICITLY sets `aria-modal="true"` on the content
panel:

```tsx
<DialogPrimitive.Content aria-modal="true" ...>
```

This is **belt-and-suspenders** — Radix sets it by default, but the
explicit pass-through ensures it survives Radix prop-merging behaviour.
Setting it explicitly is harmless and verifies the contract at the
implementation level.

**Why it matters.** `aria-modal="true"` tells AT that the content outside
this dialog is INERT — AT users should not be able to interact with
non-dialog page content while the dialog is open. AT browsers respect
this by limiting their virtual cursor to the dialog subtree.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Title + description wiring

Radix auto-wires:

- The content panel's `aria-labelledby` → the `<DialogPrimitive.Title>`'s
  generated id.
- The content panel's `aria-describedby` → the
  `<DialogPrimitive.Description>`'s generated id (when description is
  rendered).

AT users hear "[title], dialog" on focus enter, and the description is
announced as supplementary context.

**Title is mandatory.** Dialog's `title` prop is required in the Semantic
contract — every Dialog MUST have a title. Radix actually enforces this at
the development-time check (with a `<VisuallyHidden>` workaround for
visually-hidden titles, which Dialog does NOT expose in M1).

**Description is optional.** When `description` is omitted,
`aria-describedby` is not set. AT users hear only the title.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 2.4.6 Headings and Labels.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 5. Focus trap

Radix implements a strict focus trap on the dialog content:

| Key | Behaviour |
|---|---|
| Tab inside dialog | Move focus to next focusable inside the dialog. If on the last focusable, wrap to the first. |
| Shift+Tab inside dialog | Move focus to previous focusable inside the dialog. If on the first focusable, wrap to the last. |
| Click outside dialog | Radix dismisses the dialog by default (`onOpenChange(false)`); if the host overrides, the click is absorbed but focus stays trapped. |

The trap is **exhaustive** — every Tab / Shift+Tab inside the dialog
either moves focus inside the dialog or wraps. Focus cannot leave the
dialog except via Escape (which closes it) or via Radix's dismissal
behaviour (which also closes it and returns focus per §7).

**WCAG citation:** WCAG 2.2 SC 2.1.2 No Keyboard Trap — the trap is
intentional + clearly exitable via Escape, satisfying the criterion.

---

## 6. Initial focus on open

Radix's default initial-focus behaviour:

1. On open, focus moves to the first focusable element inside the dialog
   content.
2. The close `×` button in the header is typically the first focusable
   (it appears in DOM order BEFORE the body content).
3. If the host wants different initial focus (e.g., focus a specific input
   on open), Radix exposes `onOpenAutoFocus` to override.

M1 baseline: focus lands on the close button. This is acceptable but not
ideal for content-heavy dialogs (e.g., an edit form would prefer focus on
the first field, not the close button). A follow-on amendment may add an
`initialFocus?: 'close' | 'first-input' | RefObject` prop.

**WCAG citation:** WCAG 2.2 SC 2.4.3 Focus Order.

---

## 7. Return focus on close

When the dialog closes (via Escape, close button, click outside, or
`onOpenChange(false)`):

1. The trigger element that opened the dialog regains focus.
2. Radix tracks the trigger via its `<DialogPrimitive.Trigger>` (or via
   the document-active element at open time if no `<Trigger>` is used).
3. If the trigger is no longer in the DOM (e.g., the host re-renders),
   focus falls back to `<body>` — **a WCAG SC 3.2.2 violation**. Hosts
   that conditionally render the trigger must handle this manually.

**Gap G1** (§13) — the conditional-trigger focus-return failure case is
documented; hosts must guard against it.

**WCAG citation:** WCAG 2.2 SC 2.4.3 Focus Order, SC 3.2.2 On Input.

---

## 8. Escape-to-close

Radix binds the Escape key globally while the dialog is open. Pressing
Escape:

1. Fires `onOpenChange(false)`.
2. Triggers the close animation.
3. Returns focus per §7.

The host's `onOpenChange` handler is the single source of truth for the
close decision — hosts that want to confirm-on-close (e.g., "Discard
unsaved changes?") intercept here.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard — Escape provides an
alternate-input keyboard exit.

---

## 9. Close button accessible name

The close `×` icon button has:

```tsx
aria-label="Close"
```

with the `<X>` icon marked `aria-hidden="true"`. AT users hear "Close,
button" on focus enter.

**Council open question.** Should `"Close"` be passed through a host-
controlled prop for localisation? Currently the string is hard-coded in
the implementation. A future amendment SHOULD accept a `closeLabel?: string`
prop OR rely on a centralised i18n string library.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 10. Reduced motion

The animation recipe uses Tailwind's `animate-in` / `animate-out`
utilities driven by Radix's `data-[state=*]` attributes. The recipes do
NOT currently include the `motion-reduce:` variant. **Gap G2** (§13) — a
follow-on PR SHOULD add `motion-reduce:animate-none` to both the
backdrop and the content panel recipes so reduced-motion users get an
instant pop-in.

Without the `motion-reduce:` variant, the fade + zoom + slide play out
even for users who have explicitly requested reduced motion. WCAG SC 2.3.3
has no duration exemption — there is no "short animation" carve-out. The
preference must be honoured regardless of animation duration or subtlety.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 11. Color contrast

Per [Dialog.Styling §"Visual state inventory"](./Dialog.Styling.md):

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Title text on content background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Description text on content background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Close-icon on content background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Header-bottom-border / footer-top-border on content background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Close-button focus ring on content background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Backdrop on page background | not subject to contrast minimums (the backdrop dims, doesn't carry information) | — |

**Color is not the only channel.** Dialog state ("modal is open / closed")
is conveyed via:
1. `aria-modal="true"` (programmatic).
2. The focus trap (keyboard).
3. The backdrop visual (sighted users).
4. AT announcement of "[title], dialog" on open.

**WCAG SC 1.4.1 satisfied.**

---

## 12. Touch targets

| Control | Default size at M1 |
|---|---|
| Close button | `p-1` (4px padding) + `h-4 w-4` (16 × 16 icon) → effective 24 × 24 ✓ |
| Footer buttons | (host-supplied; ConfirmDialog renders `px-4 py-2` → effective ≥ 24 × 24) |

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 13. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Focus-return fails when the trigger is conditionally rendered (host removed from DOM) | Hosts must guard; future amendment may expose a `returnFocusRef?: RefObject` prop |
| G2 | Animation recipe lacks `motion-reduce:animate-none` variant | Follow-on PR adds the variant to backdrop + content recipes |
| G3 | `aria-label="Close"` is hard-coded English | Follow-on PR adds `closeLabel?: string` prop OR i18n integration |
| G4 | Initial focus is on close button by default — content-heavy dialogs (forms) need first-field focus | Follow-on amendment adds `initialFocus?` prop |
| G5 | Dialog content does NOT carry `<h1>`/`<h2>` heading semantics by default — `<DialogPrimitive.Title>` renders an `<h2>` per Radix, which may conflict with the host page's heading hierarchy | Future amendment may add `titleHeadingLevel?: 1 | 2 | 3 | 4` prop |

---

## 14. Do / Don't

### Do

- Set a meaningful `title` for every dialog — it's announced to AT on
  open and is the dialog's accessible name.
- Provide a `description` when the dialog's purpose isn't obvious from
  the title alone — AT users hear it as supplementary context.
- Use Radix's `<DialogPrimitive.Trigger>` to open the dialog when possible
  — Radix wires the focus-return automatically.
- Let Radix manage the focus trap, escape handler, and click-outside
  behaviour — do NOT override unless the host has a specific reason.
- Test the dialog with a keyboard-only workflow: Tab cycles inside, Shift+
  Tab reverses, Escape closes, focus returns to the trigger.

### Don't

- Don't omit the `aria-modal="true"` emission. Even though Radix sets it,
  the explicit pass-through is the contract guarantee.
- Don't render content outside the `<DialogPrimitive.Portal>` — inline
  rendering breaks the focus trap and the `aria-modal` semantics.
- Don't auto-focus the close `×` button when the dialog is a form — the
  user expects to start editing immediately. Use Radix's
  `onOpenAutoFocus` to override.
- Don't suppress the focus ring on the close button — keyboard users
  must be able to find it.
- Don't use Dialog for non-modal content (toasts, banners, tooltips).
  Those have separate contracts; modal semantics imply a hard interaction
  block.

---

## 15. Parity notes

- **Blazor (future HarborlineDialog track):** consumes the same
  accessibility contract. Same WAI-ARIA Dialog (Modal) pattern; Blazor
  implements focus trap manually or via a Radix-equivalent.
- **React (this contract):** as documented, with Radix UI primitives.
- **Web Components (Phase M4, Lit):** TBD; may use the native HTML
  `<dialog>` element (which has built-in modal semantics) or
  build a custom equivalent in shadow DOM.

---

## References

- ADR 0017 §A1.3 — Overlays family contract scope
- [Dialog.Semantic.md](./Dialog.Semantic.md) — prop contract
- [Dialog.Interaction.md](./Dialog.Interaction.md) — behavioural contract
- [Dialog.Styling.md](./Dialog.Styling.md) — token surface + visual states
- [ConfirmDialog.Accessibility.md](./ConfirmDialog.Accessibility.md) — composing surface
- Radix UI — `@radix-ui/react-dialog`
- WAI-ARIA Authoring Practices — Dialog (Modal) pattern
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `dialog`, `aria-modal`, `aria-labelledby`, `aria-describedby`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.6 Headings and Labels
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
