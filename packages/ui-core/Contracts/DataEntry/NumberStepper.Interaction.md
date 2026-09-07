# NumberStepper — Interaction Contract

- **Component:** NumberStepper
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberStepper.Semantic.md) · [Interaction](./NumberStepper.Interaction.md) · [Accessibility](./NumberStepper.Accessibility.md) · [Styling](./NumberStepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberStepper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how the decrement/increment buttons and the manual
input interact, including boundary disabling and disabled state.

---

## 2. Decrement button

- **Trigger:** click on the `−` button.
- **Condition:** button must not be disabled (disabled when `disabled || value <= min`).
- **Behaviour:** `onChange(clamp(value - step))`. Result is always clamped to
  `[min, max]`.

---

## 3. Increment button

- **Trigger:** click on the `+` button.
- **Condition:** button must not be disabled (disabled when `disabled || value >= max`).
- **Behaviour:** `onChange(clamp(value + step))`. Result is always clamped.

---

## 4. Manual input

- **Type:** `<input type="number">`.
- **Trigger:** change event on the input.
- **Behaviour:** `parseFloat(e.target.value)` — if not `NaN`, `onChange(clamp(parsed))`.
  If NaN (empty or non-numeric), `onChange` is NOT called (the host value is
  unchanged). The input reverts to displaying the controlled `value` on next render.
- **Native spin buttons** are suppressed via CSS
  (`[appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none
  [&::-webkit-inner-spin-button]:appearance-none`).

---

## 5. Boundary behaviour

| Condition | State |
|---|---|
| `value <= min` | Decrement button disabled; increment available |
| `value >= max` | Increment button disabled; decrement available |
| `value <= min && value >= max` | Both buttons disabled (min === max) |
| `disabled === true` | Both buttons + input disabled |

---

## 6. Keyboard behaviour

| Key | Element | Behaviour |
|---|---|---|
| Tab / Shift+Tab | All elements | Move focus: container → decrement → input → increment (document order) |
| Enter / Space | Decrement / Increment buttons | Activate button (native) |
| Up / Down arrow | Input (type=number) | Browser-native step up/down by `step`; `onChange` fires via `handleInput` |
| Digit keys | Input | Update value; `onChange` fires if valid |
| Enter | Input | Submits enclosing `<form>` |

---

## 7. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all three controls receive native `disabled`.
- Visual: `disabled:opacity-50 disabled:cursor-not-allowed` on buttons;
  `disabled:opacity-50 disabled:bg-gray-50` on input.

---

## 8. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Empty/NaN input silently ignored — host value unchanged | Emit `onChange` with a sentinel (NaN or `undefined`) so host can show error |
| I-2 | No continuous-press step (hold button down to ramp value) | Add `mousedown` + interval stepping |
| I-3 | Out-of-range `value` on mount is not clamped | Clamp initial value in host or document clearly |
