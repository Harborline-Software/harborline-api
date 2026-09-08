# AddressForm — Interaction Contract

- **Component:** AddressForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AddressForm.Semantic.md) · [Interaction](./AddressForm.Interaction.md) · [Accessibility](./AddressForm.Accessibility.md) · [Styling](./AddressForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AddressForm.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how AddressForm responds to user input across its
sub-fields — typing, selection, disabled mode, and the required constraint. It
does not cover ARIA wiring (Accessibility) or visual tokens (Styling).

---

## 2. Sub-field change handling

Each sub-field raises a native `change` event. AddressForm intercepts it and
calls `onChange({ ...value, [key]: e.target.value })`. The entire `AddressValue`
object is passed each time; only the changed key differs.

- **Text inputs:** fire on every keystroke (React synthetic `onChange`).
- **State `<select>`:** fires on selection change.

---

## 3. Field identities and `onChange` key mapping

| Field | Input type | onChange key |
|---|---|---|
| Street | `<input type="text">` | `street` |
| Street 2 | `<input type="text">` | `street2` |
| City | `<input type="text">` | `city` |
| State | `<select>` | `state` |
| ZIP | `<input type="text">` | `zip` |
| Country | `<input type="text">` | `country` |

---

## 4. Disabled mode

When `disabled === true`:

- All `<input>` and `<select>` elements receive the native `disabled` attribute.
- Inputs are non-focusable (skipped in Tab order).
- `onChange` cannot fire (inputs do not receive input events while disabled).
- Visual treatment: `disabled:bg-gray-50 disabled:text-gray-500` on each field.

---

## 5. Required constraint

When `required === true`:

- The native `required` attribute is set on the street, city, state, and ZIP
  fields.
- A red asterisk appears next to those labels (decorative, not SR-accessible
  beyond the native `required` attribute on the input).
- Native browser form-submit validation applies; AddressForm does not add
  custom validation UI in M1.
- `street2` and `country` are never `required` regardless of this prop.

---

## 6. Keyboard behaviour

AddressForm uses native HTML input semantics — no custom key handling is added.

| Key | Behaviour |
|---|---|
| Tab | Advances focus through each sub-field in DOM order: street → street2 (if shown) → city → state → zip → country (if shown). |
| Shift+Tab | Reverses focus order. |
| Enter | Submits enclosing `<form>` (browser default). |
| Arrow keys | Navigate options in the state `<select>`. |

---

## 7. ZIP field constraints

The ZIP input enforces `maxLength={10}` natively, which limits the user to
10 characters. No format mask or validation fires in M1.

---

## 8. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | No per-field error / validation feedback | Host must coordinate field errors externally |
| G2 | State list is US-only; no international address support | International addresses cannot be captured correctly |
| G3 | No `onBlur` / `onFocus` event surface | Hosts can't run blur-based validation per field |
| G4 | No field-level `error` prop; whole-form error is not a prop | Error display must be done by a wrapping host component |
