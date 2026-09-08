# NumericInput — Interaction Contract

- **Component:** NumericInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericInput.Semantic.md) · [Interaction](./NumericInput.Interaction.md) · [Accessibility](./NumericInput.Accessibility.md) · [Styling](./NumericInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how NumericInput responds to user interaction via the
increment/decrement buttons and manual input, including boundary disabling,
empty state, float precision, and disabled mode.

---

## 2. Increment button

- **Trigger:** click.
- **Condition:** not disabled and not at `max` boundary (`value < max` or `max` is
  undefined).
- **Behaviour:** `current = value === '' ? 0 : value`; `next = current + step`;
  if `max !== undefined && next > max`: no-op. Otherwise `onChange(parseFloat(next.toPrecision(10)))`.
- Note: `toPrecision(10)` is used to mitigate floating-point drift (e.g.
  `0.1 + 0.2 !== 0.3`).

---

## 3. Decrement button

- **Trigger:** click.
- **Condition:** not disabled and not at `min` boundary.
- **Behaviour:** `current = value === '' ? 0 : value`; `next = current - step`;
  if `min !== undefined && next < min`: no-op. Otherwise `onChange(parseFloat(next.toPrecision(10)))`.

---

## 4. Manual input

- **Type:** `<input type="number">`.
- **Trigger:** change event.
- **Behaviour:**
  - If `raw === ''` or `raw === '-'`: `onChange('')` fires (blank state).
  - Otherwise: `parseFloat(raw)` — if not NaN, `onChange(num)` fires (no clamping
    is applied to manual input — the host may supply out-of-range values).
  - **No clamping** on manual entry (unlike increment/decrement).

---

## 5. Boundary disabling

- Decrement: `disabled={disabled || (min !== undefined && value <= min)}`.
- Increment: `disabled={disabled || (max !== undefined && value >= max)}`.
- When `value === ''`, boundary checks use `value as number` which is treated
  as `NaN` — comparison with min/max returns false — so buttons remain enabled
  when the field is blank.

---

## 6. Disabled state

- Controlled by the spread `disabled` prop.
- When `disabled`: the entire wrapper receives `opacity-50 pointer-events-none bg-gray-50`.
  All child elements are effectively unclickable.

---

## 7. Keyboard behaviour

| Key | Behaviour |
|---|---|
| Tab / Shift+Tab | Focus moves through: decrement button → input → increment button (document order) |
| Enter / Space | Activates decrement/increment buttons |
| Up / Down arrow | Native `type="number"` increment/decrement via `step`; triggers `handleChange` |
| Digit keys | Update input; `handleChange` fires |
| Enter | Submits `<form>` |

---

## 8. Error / hint display

- When `error` (string) is provided: `<p role="alert">` with error message shown below
  the input wrapper. `hint` is suppressed. `aria-invalid={true}` on input.
- When `hint` (string) is provided and no `error`: `<p id="{id}-hint">` shown.

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Manual input not clamped — user can enter out-of-range values | Add `min`/`max` clamping to `handleChange`, or document host validation responsibility |
| I-2 | Empty/NaN on decrement/increment treated as 0 | Document as specified; could confuse if host expects empty-aware behaviour |
