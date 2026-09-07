# CurrencyField — Interaction Contract

- **Component:** CurrencyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyField.Semantic.md) · [Interaction](./CurrencyField.Interaction.md) · [Accessibility](./CurrencyField.Accessibility.md) · [Styling](./CurrencyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CurrencyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how CurrencyField responds to user input — live
character filtering, blur normalization, disabled state, and error state.

---

## 2. Typing / live change

- **Trigger:** any change to the input (keystroke, paste, autofill).
- **Filtering:** the raw input value is filtered by replacing any character
  that is not a digit (`0-9`), `.` (period), or `-` (minus) with nothing.
  The cleaned string is passed to `onChange`.
- **Per-keystroke:** `onChange` fires on every change event with the cleaned
  string. No debounce.
- **The host is responsible for numeric range validation.** CurrencyField does
  not enforce minimum or maximum values.

---

## 3. Blur normalization

- **Trigger:** the native `blur` event on the input.
- **Behaviour:** `parseFloat(value)` is called. If a valid number is produced,
  `onChange` is called with `num.toFixed(2)` — rounding to two decimal places.
  If `value` is empty, non-numeric, or produces `NaN`, no change is made.
- **Presentation:** after focus leaves, the component presents the numeric value through the
  provider's number-format seam. This formatted text is display-only; it is never emitted through
  `onChange` and never replaces the invariant stored string.
- **Host `onBlur`** (if spread via `...props`) is invoked after the
  normalization step.

**Example:**
- User types `"1234.5"` → on blur → stored value becomes `"1234.50"`.
- User types `"abc"` → on blur → no change (parseFloat returns NaN).
- User types `""` → on blur → no change.

---

## 4. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** native `disabled` on the `<input>`; no input events fire.
  `onChange` cannot fire.
- **Visual:** `cursor-not-allowed bg-gray-50 opacity-60`.

---

## 5. Error state

- **Trigger:** `error === true`.
- **Behaviour:** `aria-invalid={true}` is set. Visual: red border + red focus
  ring. Typing and focus remain enabled.
- **Error message** is provided by the parent FormField (outside this
  component).

---

## 6. Currency presentation

By default the unfocused input contains the complete locale-aware ISO-4217 display returned by the
provider seam. Focus restores the invariant raw numeric string before editing.

When `currencySymbol` is explicitly supplied, that custom string remains a non-interactive leading
adornment and the seam formats only the numeric portion. The custom path exists for backwards
compatibility; ISO-aware consumers should prefer `currency`.

---

## 7. Keyboard behaviour

| Key | Behaviour |
|---|---|
| Digit keys (`0-9`) | Inserted at caret; `onChange` fires. |
| `.` (period) | Allowed and inserted; `onChange` fires. |
| `-` (minus) | Allowed and inserted; `onChange` fires. |
| Any other key | Character stripped by the filter; `onChange` fires with unchanged cleaned string. |
| Tab / Shift+Tab | Moves focus; triggers blur normalization. |
| Enter | Submits enclosing `<form>` (browser default). |
| Arrow keys | Move caret; no `onChange`. |

---

## 8. Interaction-state precedence

1. **Disabled** — non-interactive; no callbacks.
2. **Error** — interactive; error visual + `aria-invalid`.
3. **Idle** — normal interactive state.
