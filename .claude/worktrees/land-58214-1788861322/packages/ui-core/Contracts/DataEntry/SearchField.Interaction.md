# SearchField — Interaction Contract

- **Component:** SearchField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchField.Semantic.md) · [Interaction](./SearchField.Interaction.md) · [Accessibility](./SearchField.Accessibility.md) · [Styling](./SearchField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers live value change, Enter-to-search, clear-button, and
keyDown passthrough.

---

## 2. Typing / live change

`handleChange(e)`: calls `onValueChange?.(e.target.value)` on every input
event. Per-keystroke — no debounce.

---

## 3. Enter key — search

`handleKeyDown(e)`:

1. If `e.key === 'Enter'`, calls `onSearch?.(value ?? '')`.
2. Always calls the passed-down `onKeyDown?.(e)` prop (passthrough to host).

---

## 4. Clear button

The clear button (`aria-label="Clear search"`) appears when `hasValue === true`
(`value !== undefined && value.length > 0`). On click, calls `onValueChange?.('')`.

---

## 5. HTML attribute passthrough

SearchField spreads `...props` (all `HTMLInputAttributes` minus `type` and
`onChange`) onto the native `<input>`. This allows hosts to pass `placeholder`,
`autoComplete`, `maxLength`, `name`, etc.

---

## 6. Keyboard behaviour

| Key | Behaviour |
|---|---|
| Any character | Updates value via `onValueChange`. |
| Enter | Calls `onSearch`. |
| Backspace / Delete | Updates value via `onValueChange`. |
| Tab | Focuses the input (or moves focus out). |
| (any) | Passed to host `onKeyDown` prop if provided. |

---

## 7. Interaction-state precedence

| Precedence | State |
|---|---|
| 1 | Error (`error === true`) — error border + `aria-invalid` |
| 2 | Idle — default border |

No disabled state handling in M1 — `disabled` can be passed via HTMLInputAttributes
spread but the component does not conditionally render the clear button based
on `disabled`.

---

## 8. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | Clear button renders even when input is `disabled` (via spread prop) | Clear button visible on disabled input |
| G2 | `onSearch` fires with `value ?? ''` — if `value` is uncontrolled (undefined), fires with empty string | Host should always supply `value` for predictable search |
