# NumberFormatField — Interaction Contract

- **Component:** NumberFormatField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberFormatField.Semantic.md) · [Interaction](./NumberFormatField.Interaction.md) · [Accessibility](./NumberFormatField.Accessibility.md) · [Styling](./NumberFormatField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberFormatField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes the focus/blur display swap, live filtering, disabled
state, and the prefix/suffix adornment interactions.

---

## 2. Focus — raw value display

- **Trigger:** input receives focus.
- **Behaviour:** `setFocused(true)`. The `displayValue` switches from the
  formatted value to `value` (the raw string). The user edits the raw numeric
  string without formatting.
- **Side effect:** the wrapper border changes to `border-blue-500 ring-2 ring-blue-500/20`.

---

## 3. Blur — formatted value display

- **Trigger:** input loses focus.
- **Behaviour:** `setFocused(false)`. The `displayValue` switches to
  `applyFormat(value, format)`. If `value` is empty or non-numeric, the raw
  value is shown unchanged (format is skipped when `value` is empty).
- The host does NOT receive a blur callback; blur is handled internally.

---

## 4. Live change

- **Trigger:** any input event.
- **Filtering:** `e.target.value.replace(/[^0-9.-]/g, '')` — strips everything
  except digits, `.`, and `-`.
- **Callback:** `onChange(raw)` fires with the cleaned string.

---

## 5. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** native `disabled` on the input. `onChange` cannot fire.
  Visual: `opacity-50 bg-gray-50` on wrapper.
- `disabled:cursor-not-allowed` on the input.

---

## 6. Validation state

- `required` applies the native required constraint.
- `error` is host-owned and does not change parsing or formatting.
- The input references composed FormField hint/error content through
  `aria-describedby`.
- State precedence is disabled → error → focused → idle.

---

## 7. Prefix / suffix

Prefix and suffix are read-only `<span>` elements inside the flex wrapper.
They do not intercept focus or click. The input is `flex-1 min-w-0` and
shrinks to fill the remaining space.

---

## 8. Keyboard behaviour

Standard text-input keyboard semantics. No custom key handlers.

| Key | Behaviour |
|---|---|
| Digit, `.`, `-` | Inserted; `onChange` fires. |
| Any other key | Stripped by filter; `onChange` fires with unchanged cleaned string. |
| Tab / Shift+Tab | Moves focus; triggers blur formatting. |
| Enter | Submits enclosing `<form>`. |

---

## 9. Interaction-state precedence

1. **Disabled** — non-interactive.
2. **Error** — destructive border; focused error uses the destructive ring.
3. **Focused** — raw value shown; standard focus ring.
4. **Idle** — formatted value shown; default border.
