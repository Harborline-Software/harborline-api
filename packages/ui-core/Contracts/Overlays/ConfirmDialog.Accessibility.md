# ConfirmDialog — Accessibility Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Interaction](./ConfirmDialog.Interaction.md) · [Styling](./ConfirmDialog.Styling.md)
- **Related contract:** [Dialog.Accessibility.md](./Dialog.Accessibility.md) — ConfirmDialog inherits this contract verbatim; this contract documents only the deltas.
- **Reference implementation:** `packages/ui-react/src/components/dialogs/ConfirmDialog.tsx` (composes `Dialog.tsx`)
- **Catalog row:** (no Telerik counterpart — ConfirmDialog is a Harborline-native composition)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

ConfirmDialog inherits Dialog's full accessibility contract (focus trap,
`aria-modal`, label/description wiring, escape-to-close, return-focus-on-
close, etc.). This contract names only the **deltas** introduced by
ConfirmDialog's two-button footer pattern:

1. The two buttons (Cancel + Confirm) — both native `<button
   type="button">` with inherited keyboard activation.
2. The variant axis (`default` | `destructive`) and its WCAG 1.4.1 colour-
   plus-text dual-channel rule.
3. The auto-close-on-confirm behaviour and its focus-management
   implication.
4. The `<DialogPrimitive.Title>` heading-level handoff (since ConfirmDialog
   composes Dialog directly).

For shared inherited behaviour (focus trap, `aria-modal`, etc.), see
[Dialog.Accessibility.md](./Dialog.Accessibility.md).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-
ARIA 1.2 authoring practice.

---

## 2. Inherited behaviour

The following items inherit from Dialog.Accessibility without change:

| Item | Inherited section |
|---|---|
| `role="dialog"` + `aria-modal="true"` on content | [Dialog.Accessibility §2-3](./Dialog.Accessibility.md) |
| Title and description wiring (`aria-labelledby`, `aria-describedby`) | [Dialog.Accessibility §4](./Dialog.Accessibility.md) |
| Focus trap inside the dialog | [Dialog.Accessibility §5](./Dialog.Accessibility.md) |
| Initial focus on open | [Dialog.Accessibility §6](./Dialog.Accessibility.md) |
| Return focus on close | [Dialog.Accessibility §7](./Dialog.Accessibility.md) |
| Escape-to-close | [Dialog.Accessibility §8](./Dialog.Accessibility.md) |
| Close button accessible name | [Dialog.Accessibility §9](./Dialog.Accessibility.md) |
| Reduced motion | [Dialog.Accessibility §10](./Dialog.Accessibility.md) |
| Color contrast on backdrop / title / description / header / close | [Dialog.Accessibility §11](./Dialog.Accessibility.md) |
| Touch target on close button | [Dialog.Accessibility §12](./Dialog.Accessibility.md) |

---

## 3. Cancel + Confirm button accessible names

Both buttons are native `<button type="button">` elements. Their accessible
names come from their text content (the `cancelLabel` / `confirmLabel`
props):

| Button | Default label (English) | Source |
|---|---|---|
| Cancel | `"Cancel"` | `cancelLabel` prop default |
| Confirm | `"Confirm"` | `confirmLabel` prop default |

Hosts SHOULD provide a semantic label for `confirmLabel` matching the
action: `"Delete property"` / `"Discard changes"` / `"Add tenant"` /
etc. The default `"Confirm"` is generic and works only for the trivial
case.

**Council open question.** Should the Semantic contract enforce a non-
generic `confirmLabel` when `variant === "destructive"`? Currently both
buttons default to generic English strings; localisation flows through
the prop. Recommend NOT enforcing — the discipline is a usability
convention, not an accessibility violation.

**WCAG citations:**
- WCAG 2.2 SC 2.4.6 Headings and Labels — labels SHOULD describe topic
  or purpose.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Variant axis — colour + text dual channel

The `variant` prop (`"default"` | `"destructive"`) drives the Confirm
button's background colour. **Colour alone is NOT a sufficient signal
for destructive actions** per WCAG SC 1.4.1.

The contract REQUIRES that destructive ConfirmDialogs convey the
destructive nature through TWO channels:

1. **Colour** — red Confirm button (visual; provided by Styling §2.3).
2. **Text label** — the `confirmLabel` SHOULD be a destructive verb
   ("Delete", "Discard", "Remove", "Permanently delete"). Hosts MUST
   write the label; the contract does NOT enforce.
3. **Optional third channel** — description text in the dialog ("This
   action cannot be undone").

The destructive visual alone is insufficient because:

- AT users hear only the text label (the colour is invisible).
- Colour-blind users may not distinguish red from blue.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 5. Auto-close on confirm

The shipping implementation auto-closes the dialog after the confirm
handler fires:

```typescript
function handleConfirm() {
  onConfirm()       // host's handler
  onOpenChange(false)  // close the dialog
}
```

Focus-management implication: when the dialog auto-closes, Dialog's
inherited return-focus-on-close behaviour fires, returning focus to the
trigger element. The host's `onConfirm` handler does NOT need to manage
focus.

**Async-confirm gap.** When the host's `onConfirm` is an async operation
(e.g., a network call), the dialog closes BEFORE the operation completes.
If the operation FAILS, the host must either:

1. Re-open the dialog with an error message.
2. Show a separate toast / status message.

The contract documents this as **Gap G1** (§9) — a follow-on amendment
SHOULD add a `confirmLoading?: boolean` prop that keeps the dialog open
while the host's handler is async-in-flight.

**WCAG citations:**
- WCAG 2.2 SC 3.2.2 On Input — auto-close after action is expected
  behaviour (no surprising context change).
- WCAG 2.2 SC 2.4.3 Focus Order — return focus to the trigger.

---

## 6. Cancel paths

The user can cancel in four ways, all of which fire `onOpenChange(false)`
WITHOUT invoking the host's `onConfirm`:

1. Click the Cancel button.
2. Click the close `×` button in the header.
3. Press Escape.
4. Click outside the dialog (Radix's default dismissal).

All four paths return focus per Dialog.Accessibility §7.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard — every cancel path is
keyboard-accessible (button activation is Space/Enter; Escape is keyboard).

---

## 7. Heading level handoff

`<DialogPrimitive.Title>` renders an `<h2>` by default. ConfirmDialog
passes its `title` prop through unchanged. The host's page heading
hierarchy MUST accommodate the `<h2>` — if the parent page already uses
`<h2>` for sibling sections, the ConfirmDialog's `<h2>` may conflict.

**Gap G2** (§9) — Dialog.Accessibility documents this gap. A future
amendment may add a `titleHeadingLevel?` prop on Dialog (and thus
ConfirmDialog).

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 2.4.6 Headings and Labels.

---

## 8. Touch targets

| Button | Default size at M1 |
|---|---|
| Cancel | `px-4 py-2 text-sm` → effective ~36px height ✓ |
| Confirm | `px-4 py-2 text-sm` → effective ~36px height ✓ |

Both meet the 24 × 24 minimum without spacing exceptions.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Auto-close fires before async `onConfirm` completes — host can't show "loading" or in-place error | Follow-on amendment adds `confirmLoading?: boolean` prop |
| G2 | `<h2>` heading level on title may conflict with host page hierarchy | Inherited from Dialog; future amendment adds `titleHeadingLevel?` |
| G3 | Generic `confirmLabel="Confirm"` is acceptable but suboptimal for accessibility — destructive contexts should use the destructive verb | Documented in §3 + §4; convention not enforcement |
| G4 | No `loading` indicator on Confirm button while host handler is in flight | Same path as G1 — `confirmLoading?: boolean` |

---

## 10. Do / Don't

### Do

- Write `confirmLabel` as the destructive verb when `variant ===
  "destructive"` — colour + verb together satisfy WCAG SC 1.4.1.
- Use the `destructive` variant for irreversible actions only.
- Provide a `description` — it's the entire body of the dialog (the
  body slot is intentionally empty).
- Let Dialog manage the modal mechanics (focus trap, escape, return-
  focus); ConfirmDialog ONLY contributes the action-button row.
- Test with keyboard only: Tab moves between Close → Cancel → Confirm;
  Escape closes; Space/Enter activates each button.

### Don't

- Don't use `variant="destructive"` with `confirmLabel="OK"` —
  colour-alone fails WCAG 1.4.1.
- Don't suppress auto-close — the inherited Dialog focus-return is the
  load-bearing accessibility mechanism after confirmation.
- Don't omit `description` — without it, the dialog has only a title and
  no message body.
- Don't override Radix's keyboard handling — the inherited
  Dialog.Accessibility §5-8 contracts depend on Radix's lifecycle.
- Don't add a third button without a contract amendment.

---

## 11. Parity notes

- **Blazor (future HarborlineConfirmDialog track):** consumes the same
  accessibility contract. Composes the equivalent Blazor Dialog.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD.

---

## References

- ADR 0017 §A1.3 — Overlays family contract scope
- [ConfirmDialog.Semantic.md](./ConfirmDialog.Semantic.md) — prop contract
- [ConfirmDialog.Interaction.md](./ConfirmDialog.Interaction.md) — behavioural contract
- [ConfirmDialog.Styling.md](./ConfirmDialog.Styling.md) — token surface + visual states
- [Dialog.Accessibility.md](./Dialog.Accessibility.md) — inherited modal contract
- Radix UI — `@radix-ui/react-dialog`
- WAI-ARIA Authoring Practices — Dialog (Modal) pattern
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `dialog`, `button`, `aria-modal`, `aria-labelledby`, `aria-describedby`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.6 Headings and Labels
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
