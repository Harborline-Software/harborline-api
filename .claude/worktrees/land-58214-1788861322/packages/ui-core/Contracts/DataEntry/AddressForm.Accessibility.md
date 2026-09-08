# AddressForm — Accessibility Contract

- **Component:** AddressForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AddressForm.Semantic.md) · [Interaction](./AddressForm.Interaction.md) · [Accessibility](./AddressForm.Accessibility.md) · [Styling](./AddressForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AddressForm.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

AddressForm is a self-labelled multi-field composite. Each sub-field pairs a
`<label htmlFor>` with an `<input id>` using the `idPrefix` prop for
uniqueness. The state field is a native `<select>`. This contract names the
label linkage, autocomplete signals, `required` attribute handling, and
keyboard navigation order.

---

## 2. ARIA structural roles

AddressForm renders native HTML elements exclusively; no explicit ARIA roles
are added.

| Element | Implicit role |
|---|---|
| `<input type="text">` | `textbox` |
| `<select>` | `listbox` (native select) |
| `<label>` | label association |

---

## 3. Label-input linkage

Each sub-field has a `<label htmlFor={id}>` and a matching `id` on the input
or select. The `id` is constructed as `{idPrefix}-{key}` (e.g. `addr-street`,
`addr-city`). When multiple AddressForms appear on the same page, `idPrefix`
MUST be unique per instance to avoid duplicate `id` violations (WCAG SC 4.1.1).

**Default `idPrefix`:** `'addr'` — suitable only for single-form pages.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.1 Parsing (no duplicate IDs)

---

## 4. Required fields — `required` attribute and asterisk marker

When `required === true`, the street, city, state, and ZIP fields have the
native `required` HTML attribute. The red asterisk `*` rendered next to the
label text is decorative (`<span className="text-red-500 ml-1">*</span>`) with
no `aria-label` or `aria-hidden`. This means:

- Screen readers that read the full label text do NOT hear "required" from the
  asterisk — they hear it from the native `required` attribute on the input.
- Sighted users see the asterisk; AT users hear "required" from the browser.

This is a partial gap: WCAG 1.3.1 recommends that programmatic "required"
signalling match visual signalling. The native `required` attribute covers the
programmatic channel; the asterisk is the visual channel. The mechanism is
acceptable but the asterisk should carry `aria-label="required"` or the label
text should include "(required)" for full equivalence.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 3.3.2 Labels or Instructions

---

## 5. Autocomplete attributes

All sub-fields include standard `autocomplete` token values, enabling browser
autofill and AT input-purpose signalling:

| Field | `autocomplete` value | WCAG SC 1.3.5 input purpose |
|---|---|---|
| Street | `address-line1` | Covered |
| Street 2 | `address-line2` | Covered |
| City | `address-level2` | Covered |
| State | `address-level1` | Covered |
| ZIP | `postal-code` | Covered |
| Country | `country-name` | Covered |

**WCAG citation:** WCAG 2.2 SC 1.3.5 Identify Input Purpose.

---

## 6. Keyboard navigation

Focus follows DOM order:
1. Street address
2. Street 2 (if `showStreet2 === true`)
3. City
4. State (select — use arrow keys to navigate options)
5. ZIP
6. Country (if `showCountry === true`)

No custom focus management is applied. Tab / Shift+Tab cycle through the
native focus order.

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap

---

## 7. Disabled state

When `disabled === true`, the native `disabled` attribute is set on all inputs
and the select. Disabled form elements are:

- Removed from the Tab order by the browser.
- Announced as "dimmed" or "unavailable" by AT.
- Exempt from contrast minimums (WCAG 2.2 SC 1.4.3 Notes).

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 8. Focus visible

Each sub-field inherits the `focus:ring-1 focus:ring-blue-500` recipe from the
shared `INPUT_CLS` constant. Focus is visible on all interactive sub-fields.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible.

---

## 9. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Asterisk `*` marker has no `aria-label` or `aria-hidden` — AT reads the raw `*` character | Low (native `required` attr covers programmatic signal) | Add `aria-hidden="true"` to the asterisk `<span>` |
| G2 | No `aria-describedby` for hint or error text — there is no hint/error prop at all | Medium | Requires adding error/hint props to the component |
| G3 | `idPrefix` defaults to `'addr'` — multiple instances on same page yield duplicate IDs | High (page-level defect if two instances exist) | Host MUST supply unique `idPrefix` per instance |
| G4 | Focus ring uses `ring-1` (1px) — does not meet WCAG 2.4.13 Focus Appearance | Low | Upgrade to `ring-2` in M2 pass |
