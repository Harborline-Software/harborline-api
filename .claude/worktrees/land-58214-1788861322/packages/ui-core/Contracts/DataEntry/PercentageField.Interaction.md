# PercentageField — Interaction Contract

- **Component:** PercentageField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PercentageField.Semantic.md) · [Interaction](./PercentageField.Interaction.md) · [Accessibility](./PercentageField.Accessibility.md) · [Styling](./PercentageField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PercentageField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how PercentageField handles typed input, range
clamping, disabled state, and error/hint display toggling.

---

## 2. Typing / change

- **Trigger:** any change event on the `<input type="number">`.
- **Behaviour:**
  - If `raw === ''`: `onChange('')` — blank state.
  - Otherwise: `parseFloat(raw)` → if not NaN → `onChange(Math.min(max, Math.max(min, num)))`.
    If NaN, `onChange` is not called.
- **Clamping is applied on every keystroke.** The host always receives a value
  in [min, max] or `''`.
- **Consequence:** the user cannot type `150` if `max === 100`. After typing
  `1`, `5`, `0`, the onChange fires `1`, `15`, `100` (clamped).

---

## 3. Native step

The input has `step={0.1}`. Browser Up/Down arrows increment/decrement by
0.1. `onChange` fires via `handleChange` which applies clamping.

---

## 4. Disabled state

- **Trigger:** `disabled` prop spread via `...props`.
- **Behaviour:** wrapper receives `opacity-50 pointer-events-none bg-gray-50`.
  The input receives native `disabled` via prop spread. `onChange` cannot fire.

---

## 5. Error / hint visibility

| State | Behaviour |
|---|---|
| `error` (truthy) | `<p role="alert">` shown; hint hidden; `aria-invalid={true}` |
| `hint` (truthy), no error | `<p id="{id}-hint">` shown; `aria-describedby="{id}-hint"` set |
| Neither | No sub-copy |

---

## 6. Keyboard behaviour

| Key | Behaviour |
|---|---|
| Digit keys | Update value (clamped); `onChange` fires |
| Up / Down arrows | Native `type="number"` step (0.1); `onChange` fires |
| Tab / Shift+Tab | Focus in/out |
| Enter | Submits `<form>` |

---

## 7. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Clamping on every keystroke can prevent intermediate out-of-range values | Consider clamping only on blur for better UX when typing large numbers |
| I-2 | NaN input silently ignored | Emit `onChange('')` for any non-parseable input for consistency |
