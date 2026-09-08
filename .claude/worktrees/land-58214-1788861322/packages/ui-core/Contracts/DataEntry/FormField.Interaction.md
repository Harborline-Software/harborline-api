# FormField — Interaction Contract

- **Component:** FormField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormField.Semantic.md) · [Styling](./FormField.Styling.md) · [Accessibility](./FormField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx` + `FormFieldContext.tsx`
- **Catalog rows:** #55 FieldWrapper / #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

FormField is a structural component with **no user-facing interaction surface
of its own** — it does not emit events, does not own input state, and is not
keyboard-focusable. This contract documents the runtime behaviour it does
have:

- The state mode it presents (idle vs error).
- The context-propagation step (threading `describedBy` to its child).
- The behaviour when the user interacts with the wrapped child input.

Visual styling and ARIA wiring of the label / hint / error nodes are owned by
the Styling and Accessibility contracts (PAO).

---

## 2. State modes

FormField has two interaction-relevant states:

### `idle` (no `error`, optionally with `hint`)

- The hint text (if `hint` is supplied) is rendered below the input.
- The `describedBy` threaded to the child contains the hint id only.
- The wrapped input renders in its neutral / non-error treatment.

### `error` (`error` is supplied)

- The error message is rendered below the input (as the primary supporting
  line) with `role="alert"` so AT announces it immediately on appearance.
- The hint text, if supplied, is rendered below the error as a secondary
  supporting line. Its element (`<p id="{name}-hint">`) is present in the DOM
  so the hint id in `describedBy` is never dangling. (RA-12, 2026-06-06)
- The `describedBy` threaded to the child contains both ids (`"{name}-hint
  {name}-error"`) when both hint and error are supplied.
- The child input is expected to set `aria-invalid={true}` so visual focus
  treatment matches the AT announcement. (Field components honour this via
  their own `error?: boolean` prop; see TextField / SelectField / etc.)

The transition from `idle` to `error` is **host-driven**: the host re-renders
FormField with `error` set. FormField does not own validation timing — when an
error appears or disappears is entirely up to the host's form-state machine.

---

## 3. Context propagation

On every render, FormField:

1. Computes `hintId` (`{name}-hint` when `hint` truthy, else `undefined`).
2. Computes `errorId` (`{name}-error` when `error` truthy, else `undefined`).
3. Computes `describedBy` (space-joined non-empty ids, else `undefined`).
4. Wraps `children` in a `FormFieldProvider` carrying `{ describedBy }`.

The provider is **stable across renders** only when `describedBy` is the same
string. A change to either `hint` or `error` flips the provider value, which
re-renders the child input (so `aria-describedby` updates correctly on the
input's DOM node).

---

## 4. Behaviour when the user interacts with the wrapped child

Activation of any input event on the wrapped child (focus, blur, change,
typing, dropdown open, etc.) is the **child's** responsibility. FormField does
not intercept, observe, or react to any of these events.

In particular:

- **No focus event:** FormField does not emit when its child receives focus.
- **No blur-driven error clearing:** FormField does not clear `error` on blur.
  The host's validation logic owns when the error appears and disappears.
- **No keyboard handlers:** FormField has no keyboard event listeners on its
  root.

---

## 5. Label click → input focus

The `<label>` element renders with `htmlFor={name}` and the child input's root
focusable element renders with `id={name}` (per Semantic §3.1). Clicking the
label therefore focuses the child input via standard browser label-activation
behaviour.

For the CheckboxField composition, clicking the label also **toggles** the
checkbox (standard browser behaviour for `<input type="checkbox">` paired
with a label) — see CheckboxField.Interaction.md for details on the wrapper
arrangement.

---

## 6. Required-state announcement

- The visual asterisk (`*`) renders when `required === true`. It has
  `aria-hidden="true"` so AT does not double-announce.
- The actual required-state announcement is the **child input's**
  responsibility (each field component should set `required` on its native
  control, or use a synthetic `aria-required="true"` if no native attribute
  applies).
- FormField does not pass `required` into the context. Hosts must thread it
  to the child input themselves.

This division of labour is documented for PAO Accessibility to ratify (Semantic
open question #3).

---

## 7. Interaction-state precedence

FormField has two states (see §2). When both `hint` and `error` are supplied:

1. **Error mode** — error renders as the primary supporting line; hint renders
   as the secondary supporting line (both are in the DOM). `describedBy`
   contains both ids when both are supplied. (RA-12, 2026-06-06)

---

## 8. Council open questions (Interaction)

1. **Error appearance timing.** Host-owned today. Should the contract mandate
   that an error rendered for the first time triggers focus to the field
   input (a common form-validation convention)? Or should it remain entirely
   host-managed? (Leaning: leave it to the host — form-level focus
   management is the host's domain.)
2. **Hint suppression in error mode.** Per Semantic §3.2 and §8 #2, the
   `describedBy` threading should drop the hint id when error is truthy.
   Confirm the fix before applying.
3. **Required-marker placement vs i18n.** The asterisk renders inline at the
   end of the label text. In RTL languages, should it appear at the start
   visually? This is a Styling/Accessibility concern more than an
   Interaction one; flagging for cross-contract review.

---

## Wave FR-1.4 — disabled context propagation (Interaction)

_Ruling: FR-1.4 (family-rulings-2026-06-11.md). Pattern: DataGrid #1022._
_Status: Draft._

### FR-1.4.1 Context-propagation step update

The context-propagation step in §3 gains two new fields on every render:

1. Computes `hintId` (`{name}-hint` when `hint` truthy, else `undefined`).
2. Computes `errorId` (`{name}-error` when `error` truthy, else `undefined`).
3. Computes `describedBy` (space-joined non-empty ids, else `undefined`).
4. **NEW** — resolves `context.required` (`required ?? false`).
5. **NEW** — resolves `context.disabled` (`disabled ?? false`).
6. Wraps `children` in a `FormFieldProvider` carrying `{ id, describedBy, required, disabled }`.

Steps 4 and 5 are cheap boolean resolutions; they do not alter the re-render
budget of the existing steps.

### FR-1.4.2 disabled — interaction-state implications

When `disabled === true`:

- FormField itself has no interaction surface, so no FormField-level behaviour
  changes (FormField is not keyboard-focusable, does not handle pointer events).
- The context value `disabled: true` propagates to child inputs, which each
  apply their own disabled treatment (see per-component Interaction contracts).
- FormField does NOT add `pointer-events: none` or any other blocking CSS to
  its slot wrapper — child inputs own their disabled pointer treatment.

This preserves FormField's structural neutrality: the wrapper passes state
through; it does not intercept or override child input behaviour.

### FR-1.4.3 required — interaction-state implications

The `required` prop already drives the visual asterisk (M1). FR-1.4 adds
context propagation only. No change to FormField's own interaction behaviour.

Child inputs that now read `required` from context must emit `aria-required`
and may affect their own validation triggers — see each component's
Interaction contract for wave-FR-1.4 notes when those are authored.

### FR-1.4.4 Consumption rule reminder

Child inputs apply logical OR (Semantic FR-1.4.3):

```typescript
const effectiveDisabled = props.disabled || ctxDisabled
const effectiveRequired = props.required || ctxRequired
```

This means a FormField can be made entirely non-interactive by setting
`disabled={true}` on the FormField alone, without touching each child input's
props. The context acts as a field-group disability flag.
