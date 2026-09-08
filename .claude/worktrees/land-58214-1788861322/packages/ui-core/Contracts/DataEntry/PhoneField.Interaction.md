# PhoneField — Interaction Contract

- **Component:** PhoneField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PhoneField.Semantic.md) · [Interaction](./PhoneField.Interaction.md) · [Accessibility](./PhoneField.Accessibility.md) · [Styling](./PhoneField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PhoneField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes live formatting, digit filtering, disabled state, and
error/hint display for PhoneField.

---

## 2. Live formatting

- **Trigger:** any change event on the `<input type="tel">`.
- **Behaviour:**
  1. Strip all non-digit characters from the input.
  2. Cap at 10 digits.
  3. Format via `formatPhone(raw)`:
     - 0–3 digits: `"NXX"`.
     - 4–6 digits: `"(NXX) NXX"`.
     - 7–10 digits: `"(NXX) NXX-XXXX"`.
  4. `setDisplayValue(formatted)` — updates the visible input.
  5. `onValueChange(raw, formatted)` — notifies the host.

---

## 3. Formatting examples

| User types | Raw digits | Displayed |
|---|---|---|
| `5` | `"5"` | `"5"` |
| `555` | `"555"` | `"555"` |
| `5551` | `"5551"` | `"(555) 1"` |
| `5551234` | `"5551234"` | `"(555) 123-4"` |
| `5551234567` | `"5551234567"` | `"(555) 123-4567"` |

---

## 4. Country code prefix `+1`

A non-interactive `<span>` with text `"+1"` is positioned at the left of the
input. It does not intercept input events. The input has `pl-9` padding to
avoid text overlap.

---

## 5. Disabled state

- **Trigger:** `disabled` spread via `...props` to the input.
- **Visual:** `disabled:bg-gray-50 disabled:text-gray-500 disabled:cursor-not-allowed`.

---

## 6. Error / hint

| State | Behaviour |
|---|---|
| `error` truthy | `<p role="alert">` shown; hint hidden; `aria-invalid={true}` |
| `hint` truthy, no error | hint `<p>` shown; `aria-describedby` set |

---

## 7. Keyboard behaviour

Standard `<input type="tel">` keyboard semantics. No custom key handlers.

| Key | Behaviour |
|---|---|
| Digit keys | Inserted; formatting applied; `onValueChange` fires |
| Non-digit keys | Stripped; display unchanged; `onValueChange` fires with same raw |
| Backspace | Removes the last character from the raw digit string; reformats |
| Tab / Shift+Tab | Focus in/out |
| Enter | Submits `<form>` |

---

## 8. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Paste of formatted numbers (e.g. `"(555) 123-4567"`) is handled — non-digits stripped | Working correctly |
| I-2 | Backspace on formatted string may feel inconsistent (e.g. deleting the `-` or `)` re-triggers `onChange` with the same digit string) | Known UX limitation of uncontrolled formatting; consider a masked input library |
| I-3 | `value` prop changes post-mount not reflected in display | Semi-controlled gap; fix in Semantic S-1 |
