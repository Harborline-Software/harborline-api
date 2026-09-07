# AddressForm — Styling Contract

- **Component:** AddressForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AddressForm.Semantic.md) · [Interaction](./AddressForm.Interaction.md) · [Accessibility](./AddressForm.Accessibility.md) · [Styling](./AddressForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AddressForm.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

AddressForm defines two shared Tailwind class constants — `INPUT_CLS` and
`LABEL_CLS` — applied uniformly to all sub-fields. This contract names those
recipes and their token equivalents.

---

## 2. Shared class constants

### 2.1 `INPUT_CLS` — applied to every `<input>` and `<select>`

```
w-full rounded-md border border-gray-300 bg-white
px-3 py-2 text-sm text-gray-900 placeholder-gray-400
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
disabled:bg-gray-50 disabled:text-gray-500
```

### 2.2 `LABEL_CLS` — applied to every `<label>`

```
block text-sm font-medium text-gray-700 mb-1
```

---

## 3. Token surface

AddressForm shares the `--sf-input-*` family from the TextField Styling
contract. No AddressForm-specific tokens are introduced.

| CSS class | Token mapping |
|---|---|
| `border-gray-300` | `--sf-input-border` |
| `focus:border-blue-500 focus:ring-blue-500` | `--sf-input-border-focus` + `--sf-input-ring-focus` |
| `bg-white` | `--sf-input-bg` |
| `text-gray-900` | `--sf-input-fg` |
| `placeholder-gray-400` | `--sf-input-placeholder-fg` |
| `disabled:bg-gray-50 disabled:text-gray-500` | `--sf-input-bg-disabled` |
| `text-gray-700` (label) | `--sf-label-fg` |

---

## 4. Layout

- Root: `space-y-3` vertical stack with optional `className` override.
- City + State row: `grid grid-cols-2 gap-3` with city spanning `col-span-2 sm:col-span-1` (full width below `sm`, half-width at `sm+`).
- ZIP + Country row: plain `<div>` (no grid) when country hidden; `grid grid-cols-2 gap-3` when `showCountry === true`.

---

## 5. Visual state inventory

| State | Condition | Affected region | Recipe |
|---|---|---|---|
| **default** | enabled, no focus | border | `border-gray-300` |
| **focus-visible** | keyboard or pointer focus | border + ring | `focus:border-blue-500 focus:ring-1 focus:ring-blue-500` |
| **disabled** | `disabled === true` | background + text | `disabled:bg-gray-50 disabled:text-gray-500` |
| **required asterisk** | `required === true` on label | inline span | `text-red-500 ml-1` |

No error state styling is built into AddressForm in M1. Error indication is
host-owned.

---

## 6. Known gaps

- No size variants (all sub-fields are hard-coded to the `md` TextField size: `px-3 py-2 text-sm`).
- No error-border treatment (no `error` prop exists).
- `ring-1` focus ring does not meet WCAG 2.4.13 Focus Appearance; upgrade to `ring-2` in M2.
