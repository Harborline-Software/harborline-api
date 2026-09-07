# CreditCardField — Styling Contract

- **Component:** CreditCardField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CreditCardField.Semantic.md) · [Interaction](./CreditCardField.Interaction.md) · [Accessibility](./CreditCardField.Accessibility.md) · [Styling](./CreditCardField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CreditCardField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CreditCardField renders four sub-inputs in a vertical stack (number, name,
expiry + CVC in a 2-column row). Each input uses the shared `--sf-input-*`
token family (identical to TextField). There is no size axis — the component
renders at a fixed `md`-equivalent density.

---

## 2. Token surface

CreditCardField reuses the `--sf-input-*` token family defined in TextField.Styling.
No new tokens are introduced by this component.

| Token consumed | Usage |
|---|---|
| `--sf-input-bg` | Input background |
| `--sf-input-fg` | Input text |
| `--sf-input-placeholder-fg` | Placeholder text |
| `--sf-input-border` | Default border |
| `--sf-input-border-focus` | Focused border |
| `--sf-input-ring-focus` | Focused ring |
| `--sf-input-bg-disabled` | Disabled background |

Label styling is standalone (`text-sm font-medium text-gray-700`). No token
is defined for sub-field labels; PAO should align these with the FormField
label token (`--sf-field-label-fg`).

---

## 3. Tailwind class recipes

### 3.1 Root container

```
space-y-3
```

### 3.2 Sub-field wrapper (number, name, expiry, CVC)

```
(each sub-field has its own containing <div>)
```

Expiry + CVC row:

```
grid grid-cols-2 gap-3
```

### 3.3 Label

```
block text-sm font-medium text-gray-700 mb-1
```

### 3.4 Input (shared recipe for all four inputs)

```
w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900
placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1
focus:ring-blue-500 disabled:bg-gray-50
```

This is the standard `md`-size input recipe (equivalent to `--sf-input-padding-md`).

### 3.5 Card number field — network badge

```
absolute right-3 top-1/2 -translate-y-1/2 text-xs font-semibold text-gray-500
```

The badge is absolutely positioned inside a `relative` container wrapping the
card number input. It is `pointer-events-none` (visually overlaid, no blocking).

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Focus | keyboard/pointer focus | `focus:border-blue-500 focus:ring-1 focus:ring-blue-500` |
| Disabled | `disabled === true` | `disabled:bg-gray-50` + native disabled muting |
| Error | `error === true` | `border-destructive focus-visible:ring-destructive` on each sub-input |
| Network badge visible | network ≠ `'unknown'` | Absolutely positioned `text-xs text-gray-500` badge |

Error styling reuses the shared semantic `destructive` tokens. It introduces
no raw color and does not change layout.

---

## 5. Open questions

1. **Size axis.** A `size` prop matching TextField's `sm | md | lg` would
   allow CreditCardField to be used in compact form layouts.
2. **Expiry/CVC layout.** The 2-column grid is hardcoded. Some designs prefer
   a single row (number on top, then name | expiry | CVC in one row).
