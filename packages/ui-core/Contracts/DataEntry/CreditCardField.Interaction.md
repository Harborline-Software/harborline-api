# CreditCardField — Interaction Contract

- **Component:** CreditCardField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CreditCardField.Semantic.md) · [Interaction](./CreditCardField.Interaction.md) · [Accessibility](./CreditCardField.Accessibility.md) · [Styling](./CreditCardField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CreditCardField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how each sub-field of CreditCardField responds to user
input, including digit filtering, formatting, and network-adaptive constraints.

---

## 2. Card number field

- **Input mode:** `numeric` (opens numeric keypad on mobile).
- **Filtering:** non-digit characters are stripped on every change event before
  storing the raw value.
- **Raw digit cap:** 16 digits for Visa/Mastercard/Discover; 15 digits for Amex
  (detected from current prefix).
- **Display formatting:** raw digits are formatted for display as groups
  separated by spaces. Standard: 4-4-4-4. Amex: 4-6-5.
- **`maxLength`** on the input reflects the formatted length (19 for standard,
  17 for Amex) to prevent typing beyond the limit in the formatted view.
- **Network detection:** fires on every change; drives CVC length constraint
  and network badge display. Amex is detected at `^3[47]`; Visa at `^4`;
  Mastercard at `^5[1-5]` or `^2[2-7]`; Discover at `^6(?:011|5)`.

---

## 3. Name on card field

- **Input mode:** standard text.
- **Filtering:** none — the value is passed through verbatim.
- **No case normalisation.** The host receives the name exactly as typed.

---

## 4. Expiry field

- **Input mode:** `numeric`.
- **Filtering:** non-digits are stripped; capped at 4 raw digits (MMYY).
- **Display formatting:** a `/` separator is inserted after the first 2 digits
  when there are more than 2 digits present. Display: `MM/YY`.
- **`maxLength`:** 5 (MM/YY including the slash).
- No month/year range validation is performed by the component.

---

## 5. CVC field

- **Input mode:** `numeric`.
- **Filtering:** non-digits are stripped.
- **Raw digit cap:** 3 for standard networks; 4 for Amex.
- **`maxLength`** mirrors the raw digit cap.
- **Placeholder:** `"123"` for standard, `"1234"` for Amex.

---

## 6. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all four inputs receive the native `disabled` attribute.
  Inputs are non-focusable, non-interactive. `onChange` cannot fire.
- **Visual:** `disabled:bg-gray-50` on each input.

---

## 7. Validation state

- `required` applies the native required constraint to all four sub-inputs.
- `error` is a host-owned composite state; it does not run Luhn, expiry, or
  CVC validation internally.
- Error messages stay in FormField/ValidationMessage composition and are
  addressed from every sub-input through `aria-describedby`.
- State precedence is disabled → error → focused → idle.

---

## 8. Keyboard behaviour

Each sub-field is a native `<input>` with standard text-input keyboard
semantics. No custom keyboard handlers are implemented.

| Key | Behaviour |
|---|---|
| Tab / Shift+Tab | Moves focus between sub-fields in document order: number → name → expiry → CVC. |
| Arrow keys | Move the caret within the field. |
| Enter | Submits the enclosing `<form>` (browser default). |

There is no automatic focus-advance from one field to the next when the
maximum length is reached.

> **Known gap I-1:** No auto-advance to the next field when a field reaches
> its max length. Many card-entry UIs advance focus automatically from the
> number field to name to expiry to CVC. This is not implemented.

---

## 9. Network badge

When the card network is detected (not `'unknown'`), a read-only text badge
(`"Visa"`, `"Mastercard"`, etc.) is rendered as an absolutely-positioned
`<span>` inside the card number field. It is `pointer-events-none` and does
not interfere with input interaction.

---

## 10. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | No auto-advance between sub-fields on max-length reached | Add `maxLength` detection + programmatic focus |
| I-3 | No Luhn check on blur | Host responsibility; document clearly |
| I-4 | Month/year range validation on expiry not performed | Host responsibility or add `onBlur` validation callback |
