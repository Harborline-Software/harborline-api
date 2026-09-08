# SelectField — Interaction Contract

- **Component:** SelectField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectField.Semantic.md) · [Styling](./SelectField.Styling.md) · [Accessibility](./SelectField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SelectField.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

SelectField is built on Radix UI's `@radix-ui/react-select` primitive — most
keyboard navigation and popover lifecycle behaviour is inherited from Radix and
documented at a high level here. This contract describes the **public
behavioural surface** the host sees, plus the SelectField-specific state
treatments (error, disabled).

Visual styling and ARIA wiring are owned by Styling and Accessibility (PAO).

---

## 2. Open / close

- **Trigger to open:**
  - Click the `<Select.Trigger>`.
  - Focus the trigger and press Space, Enter, Down arrow, or Up arrow.
- **Popover position:** `position="popper"` with `sideOffset={4}` — the
  popover renders below the trigger by default, flipping to above if there's
  insufficient space below.
- **Popover width:** bound to the trigger width via
  `min-w-[var(--radix-select-trigger-width)]`.
- **Close triggers (Radix-default):**
  - Click outside the popover.
  - Press Escape.
  - Select an option.
  - Tab out (focus moves to next/prev tabstop and the popover closes).
- **No `onOpenChange` callback in M1.** Hosts cannot observe popover
  open/close transitions; deferred per Semantic §7.

---

## 3. Selection

- **Trigger:** click an option in the open popover, or focus an option via
  keyboard and press Enter or Space.
- **Callback:** `onValueChange(option.value)` fires once, then the popover
  closes (Radix default).
- **Same-value re-select:** Radix does **not** fire `onValueChange` when the
  user re-selects the already-current value. Hosts cannot detect a
  "confirmed same value" event in M1.
- **No `value === ''` user gesture:** the empty string is the "no selection"
  state, but there is no built-in clear affordance in the popover. To clear,
  the host must programmatically pass `value=''`.

---

## 4. Disabled mode

- **Trigger:** `disabled === true`.
- **Behaviour:**
  - Radix `<Select.Root disabled>` makes the trigger non-interactive and
    the popover cannot open.
  - Trigger is removed from tab order.
  - Visual: `cursor-not-allowed bg-gray-50 opacity-60`. Tokens owned by
    PAO Styling.

---

## 5. Error mode

- **Trigger:** `error === true`.
- **Behaviour:**
  - `aria-invalid={true}` is set on the trigger.
  - Trigger renders red border + red focus ring.
  - The error message is the parent FormField's job.
  - The popover and option-selection behaviour are otherwise unchanged.

---

## 6. Keyboard behaviour (Radix-derived)

| Key | Behaviour |
| --- | --- |
| Space / Enter (trigger focused, closed) | Opens the popover. |
| Down / Up arrow (trigger focused, closed) | Opens the popover and moves focus to the next / previous option. |
| Down / Up arrow (popover open) | Moves the highlight up/down the options. |
| Enter / Space (popover open, option highlighted) | Selects that option; fires `onValueChange`; closes the popover. |
| Escape (popover open) | Closes the popover without selecting; returns focus to the trigger. |
| Home / End (popover open) | Highlights the first / last option. |
| Type-ahead (popover open) | Typing a letter highlights the first option whose label starts with that letter (Radix-default). |
| Tab / Shift+Tab (trigger focused) | Moves focus out; closes the popover. |

These are Radix defaults; SelectField does not add or override.

---

## 7. Interaction-state precedence

When multiple state props are set, resolve in this order (highest precedence
first):

1. **Disabled** (`disabled === true`) — trigger non-interactive; popover
   cannot open; not in tab order.
2. **Error** (`error === true`, not disabled) — interactive; error visual
   treatment + `aria-invalid`.
3. **Idle** — normal interactive state.

The popover open/closed state is orthogonal to these — error mode doesn't
prevent opening; disabled prevents opening.

---

## 8. Council open questions (Interaction)

1. **`onOpenChange` callback.** Often needed for analytics or for syncing
   focus with an external control. Add in M1 or fast-follow?
2. **Type-ahead behaviour with non-Latin scripts.** Radix's type-ahead is
   prefix-match on the option label. For non-Latin scripts (CJK, Arabic)
   the match heuristic may behave unexpectedly. PAO Accessibility should
   confirm; no SelectField-level change required.
3. **Same-value re-select event.** Some hosts want to track "user confirmed
   the existing value" (e.g. an explicit click on the currently-selected
   option) as a distinct gesture. Radix does not emit it. Worth carrying
   a custom solution, or out of scope?
4. **Empty-options state.** When `options` is empty or undefined, the trigger renders but the popover has no options. No placeholder or "no results" state is specified. The behavior is currently undefined — the popover opens and shows nothing. Requires spec decision: render nothing, render a disabled "No options" item, or hide the trigger entirely?
