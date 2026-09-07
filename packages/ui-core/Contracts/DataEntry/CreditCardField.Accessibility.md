# CreditCardField — Accessibility Contract

- **Component:** CreditCardField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CreditCardField.Semantic.md) · [Interaction](./CreditCardField.Interaction.md) · [Accessibility](./CreditCardField.Accessibility.md) · [Styling](./CreditCardField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CreditCardField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CreditCardField renders four native `<input>` elements, each with an explicit
`<label>` element. This contract names the label linkage, `autocomplete`
semantics, and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<input id="cc-number">` | implicit `textbox` | Native text input |
| `<input id="cc-name">` | implicit `textbox` | Native text input |
| `<input id="cc-expiry">` | implicit `textbox` | Native text input |
| `<input id="cc-cvc">` | implicit `textbox` | Native text input |

All four inputs are native `<input type="text">` elements. AT announces them
with their label text.

---

## 3. Label-input linkage

Each sub-field has a corresponding `<label htmlFor={...}>` element:

| `<label>` text | `htmlFor` | Input `id` |
|---|---|---|
| "Card number" | `cc-number` | `cc-number` |
| "Name on card" | `cc-name` | `cc-name` |
| "Expiry" | `cc-expiry` | `cc-expiry` |
| "CVC" | `cc-cvc` | `cc-cvc` |

AT announces the label on focus. Click on label focuses the input.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 4.1.2.

> **Gap A-1:** Hardcoded `id` values (`cc-number`, `cc-name`, `cc-expiry`,
> `cc-cvc`). When CreditCardField is used multiple times on a page, duplicate
> ids cause incorrect label linkage. Fix: derive ids from an `id` prop or
> `useId()`.

---

## 4. autocomplete attributes

All four inputs use the correct `autocomplete` token per WCAG SC 1.3.5
(Identify Input Purpose):

| Input | `autocomplete` |
|---|---|
| Card number | `cc-number` |
| Name on card | `cc-name` |
| Expiry | `cc-exp` |
| CVC | `cc-csc` |

**WCAG citation:** WCAG 2.2 SC 1.3.5 Identify Input Purpose.

---

## 5. Validation state

`error={true}` emits `aria-invalid="true"` on all four inputs and applies the
destructive border/focus treatment. `required={true}` applies native
`required` to all four inputs. Both states follow FR-1's composite-field
semantics.

When composed inside FormField, its `describedBy` value is applied to all four
inputs. The FormField error element uses `role="alert"`, so newly rendered
validation text is announced and remains discoverable when any sub-input is
focused. Validation text is resolved from a stable catalog code; the component
does not accept an English `validationMessage` string.

**WCAG citations:** WCAG 2.2 SC 3.3.1, SC 3.3.2, SC 4.1.2.

---

## 6. Disabled state

Native `disabled` attribute is set on all four inputs when `disabled === true`.
AT announces inputs as "unavailable" / "dimmed". Inputs are removed from tab
order (native browser behaviour).

**WCAG citation:** WCAG 2.2 SC 4.1.2.

---

## 7. Network badge

The network badge (`"Visa"`, `"Mastercard"`, etc.) is a `<span>` with no ARIA
role or label. It is informational text visible to sighted users.

> **Gap A-3:** The network badge is accessible via DOM text content but has no
> `role="status"` or `aria-live` announcement when it changes (e.g., network
> changes from `unknown` to `Visa` as digits are typed). AT users receive no
> live region update. Fix: add `aria-live="polite"` to the badge span.

---

## 8. Keyboard navigation

Tab order: card number → name → expiry → CVC (document order). No custom
keyboard handlers. Standard text-input semantics per input.

**WCAG citations:** WCAG 2.2 SC 2.1.1, SC 2.1.2.

---

## 9. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Hardcoded ids cause duplicate-id failures on multi-instance pages | WCAG SC 4.1.1, SC 1.3.1 | Use `useId()` or accept `id` prop |
| A-3 | Network badge has no `aria-live` announcement | WCAG SC 4.1.3 (Status Messages) | Add `role="status"` or `aria-live="polite"` to badge |
