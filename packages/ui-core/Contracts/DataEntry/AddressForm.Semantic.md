# AddressForm — Semantic Contract

- **Component:** AddressForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./AddressForm.Interaction.md) · [Accessibility](./AddressForm.Accessibility.md) · [Styling](./AddressForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AddressForm.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — composite form wrapping Input + SelectField (no Radix direct dependency)

---

## 1. Purpose

AddressForm is a multi-field composite that captures a US postal address as a
controlled value object. It renders inline labels and native `<input>` /
`<select>` fields for street, optional street2 line, city, state (US dropdown),
ZIP, and optional country. It does not use `FormField` wrapping — labels and
inputs are managed locally.

---

## 2. Data model

AddressForm is fully controlled: the host owns the `AddressValue` object and
receives updates via `onChange`.

```typescript
interface AddressValue {
  street: string
  street2?: string
  city: string
  state: string
  zip: string
  country?: string
}

interface AddressFormProps {
  value: AddressValue
  onChange: (value: AddressValue) => void
  disabled?: boolean
  showStreet2?: boolean
  showCountry?: boolean
  required?: boolean
  idPrefix?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `AddressValue` | _required_ | Controlled address object. All sub-fields are strings; `street2` and `country` are optional (may be undefined). |
| `onChange` | `(value: AddressValue) => void` | _required_ | Called with the new `AddressValue` after any sub-field changes. |
| `disabled` | `boolean` | `false` | When `true`, all sub-fields are non-interactive. |
| `showStreet2` | `boolean` | `true` | When `true`, the optional second address line field is rendered. |
| `showCountry` | `boolean` | `false` | When `true`, a freeform country text input is rendered alongside ZIP. |
| `required` | `boolean` | `false` | When `true`, `required` HTML attribute is set on street, city, state, and ZIP fields. A red asterisk `*` appears next to those field labels. |
| `idPrefix` | `string` | `'addr'` | Prefix used to construct each sub-field's `id` attribute (e.g. `addr-street`, `addr-city`). Allows multiple AddressForms on one page without `id` collisions. |
| `className` | `string` | `''` | Additional CSS classes applied to the root `<div>`. |

### 3.1 Sub-field layout

The component renders a vertical stack (`space-y-3`) with the following logical
structure:

1. Street address (`<input type="text">`)
2. Street 2 (`<input type="text">`) — rendered only when `showStreet2 === true`
3. City + State in a 2-column responsive grid
4. ZIP (+ Country when `showCountry === true`) in a 2-column grid when country shown

### 3.2 State dropdown

State is a `<select>` with the 50 US states plus DC as options. The first
option value is `""` rendered as `"—"` (empty sentinel). No internationalization
of the state list exists in M1.

### 3.3 ZIP field specifics

The ZIP `<input>` sets `inputMode="numeric"` and `maxLength={10}` for
formatting guidance. It does not validate ZIP format; format validation is
host-owned.

### 3.4 autoComplete attributes

All sub-fields set standard HTML `autocomplete` attribute values:

| Sub-field | autoComplete value |
|---|---|
| Street | `address-line1` |
| Street 2 | `address-line2` |
| City | `address-level2` |
| State | `address-level1` |
| ZIP | `postal-code` |
| Country | `country-name` |

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `AddressValue` | Any sub-field changes. The entire value object is sent; only the changed key differs. |

No `onBlur`, `onFocus`, or field-level error callbacks exist in M1.

---

## 5. Variants and states

| State | Trigger |
|---|---|
| **Enabled** | default |
| **Disabled** | `disabled === true` — all sub-fields non-interactive |
| **Required** | `required === true` — required attribute set on key fields; visual asterisk rendered |
| **Partial display** | `showStreet2 === false` hides street 2 row; `showCountry === true` shows country |

---

## 6. Composition

AddressForm is self-contained — it manages its own label markup internally.
It does NOT use `FormField` or `FieldWrapper`. Hosts that want an outer
FormField-style wrapper around the full composite must compose that themselves.

---

## 7. Deferred features

- **Per-field error display.** No field-level `error` prop. Hosts must coordinate
  error messages externally.
- **International address formats.** Only US-centric layout and state dropdown.
- **Zip code validation.** Format validation is host-owned.
- **Street 2 toggle.** Only visibility control; no add/remove UX affordance.
- **Autocomplete from third-party address APIs.**
