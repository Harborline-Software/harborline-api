# DateField — Interaction Contract

- **Component:** DateField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateField.Semantic.md) · [Styling](./DateField.Styling.md) · [Accessibility](./DateField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateField.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

DateField wraps a native `<input type="date">`. Most behaviour — keyboard
navigation, picker UX, AT announcements — is browser-owned. This contract
documents the DateField-specific surface (controlled value, error / disabled
treatments, min / max bounds) and the high-level browser-native behaviour the
host can rely on.

Visual styling and ARIA wiring are owned by Styling and Accessibility (PAO).

---

## 2. Date selection

- **Trigger:**
  - Clicking the field opens the browser-native picker (browser-defined
    affordance — Chrome shows a calendar icon and / or makes the whole
    field clickable; Firefox uses a slightly different UX).
  - Focusing the field via Tab and pressing Space / Down arrow may also
    open the picker (browser-dependent).
  - Direct keyboard typing into the date segments works in browsers that
    support it (Chromium-based).
- **Callback:** `onChange(e.target.value)` fires when the user picks a date
  in the picker, or types a complete valid date, or clears the field.
  Payload is the new ISO-8601 string (`YYYY-MM-DD`) or `""` when cleared.
- **Per-keystroke vs commit:** for the typed-input path, browsers typically
  fire the `change` event only when the date is complete and valid — so
  `onChange` does not fire on every digit. For the picker path, `onChange`
  fires once per selection.

---

## 3. Min / max bounds

- **Trigger:** `min` / `max` props supplied.
- **Behaviour:** the browser disables out-of-range dates in the picker.
  Direct typing of an out-of-range date is still possible in some browsers;
  on form submission the browser flags `validity.rangeUnderflow` /
  `rangeOverflow`.
- **DateField does not enforce bounds in onChange.** Hosts that want to
  reject out-of-range values must validate themselves and surface the
  result via FormField's `error` prop.

---

## 4. Disabled mode

- **Trigger:** `disabled === true`.
- **Behaviour:**
  - Native `disabled` attribute on the input.
  - Not focusable via Tab.
  - Click does not open the picker.
  - Visual: `cursor-not-allowed bg-gray-50 opacity-60`.

---

## 5. Error mode

- **Trigger:** `error === true`.
- **Behaviour:**
  - `aria-invalid={true}` set on the input.
  - Red border + red focus ring.
  - The picker behaviour is otherwise unchanged.
  - Error message comes from the parent FormField.

---

## 6. Keyboard behaviour (browser-derived)

Specific keyboard semantics are browser-owned and may differ across
Chromium / Firefox / Safari. The high-level user contract:

| Key (Chromium baseline) | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Move focus into / out of the field. |
| Space / Down arrow | Open the picker (browser-dependent). |
| Direct number keys | Type into the date segments (mm / dd / yyyy navigation by segment). |
| Arrow keys (focused on a date segment) | Increment / decrement the segment value. |
| Escape (picker open) | Close the picker without changing the value. |
| Enter (picker open) | Confirm the highlighted date. |
| Backspace / Delete | Clear the field (browser-dependent — may clear segment-by-segment). |

PAO Accessibility owns the formal AT testing matrix across browsers + screen
readers. The DateField contract is "the host delegates to the browser-native
picker, and trusts the browser's AT integration."

---

## 7. Interaction-state precedence

When multiple state props are set, resolve in this order (highest precedence):

1. **Disabled** — input non-interactive; picker cannot open.
2. **Error** — interactive; error visual treatment + `aria-invalid`.
3. **Idle** — normal interactive state.

---

## 8. Council open questions (Interaction)

1. **Native picker keyboard parity.** Across browsers, the picker keyboard
   behaviour varies (Firefox's date picker has historically had AT issues
   that other browsers don't). PAO Accessibility should confirm whether
   this is acceptable for M1 or whether a custom picker is needed sooner.
2. **`onBlur` for validation.** Should DateField expose `onBlur` so hosts
   can validate the typed-but-incomplete state? Today there's no callback
   between "user starts typing" and "user finishes a valid date."
3. **Clearing UX.** Browsers handle "clear" differently. Should DateField
   add an explicit clear button (slot or built-in) so the UX is
   consistent across browsers? Deferred per Semantic §7, but worth a
   council call.
