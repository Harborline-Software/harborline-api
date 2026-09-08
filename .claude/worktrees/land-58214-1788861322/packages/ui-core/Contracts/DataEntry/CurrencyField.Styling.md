# CurrencyField — Styling Contract

- **Component:** CurrencyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CurrencyField.Semantic.md) · [Interaction](./CurrencyField.Interaction.md) · [Accessibility](./CurrencyField.Accessibility.md) · [Styling](./CurrencyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CurrencyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CurrencyField is a text input whose normal path renders the complete locale-aware currency string
inside the input. It shares the `--sf-input-*` token surface with TextField. An explicit custom
`currencySymbol` enables the backwards-compatible leading-adornment zone. There is no size axis —
one density (`md`-equivalent).

---

## 2. Token surface

CurrencyField reuses the `--sf-input-*` family. Additional token for the
adornment:

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-input-bg` | Input background | `white` |
| `--sf-input-fg` | Input text colour | `gray-900` |
| `--sf-input-placeholder-fg` | Placeholder text | `gray-400` |
| `--sf-input-border` | Default border | `gray-300` |
| `--sf-input-border-focus` | Focused border | `blue-500` |
| `--sf-input-ring-focus` | Focused ring | `blue-500` |
| `--sf-input-border-error` | Error border | `red-400` |
| `--sf-input-ring-focus-error` | Error focus ring | `red-500` |
| `--sf-input-bg-disabled` | Disabled background | `gray-50` |
| `--sf-input-opacity-disabled` | Disabled opacity | `0.6` |
| `--sf-currency-symbol-fg` | Symbol text colour | `gray-500` |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
relative
```

(plus any host-supplied `className`)

### 3.2 Custom currency symbol adornment

```
pointer-events-none absolute inset-y-0 left-3 flex items-center text-sm text-gray-500
```

### 3.3 Input

```
block w-full rounded-md border bg-white px-3 py-2 text-sm transition-colors
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1
```

When `currencySymbol` is supplied, `ps-7 pe-3` replaces `px-3` to create space for the custom
leading adornment using logical properties.

### 3.4 Error overlay

```
border-red-400 focus:ring-red-500
```

Replaces default border and focus ring.

### 3.5 Default (non-error) overlay

```
border-gray-300 focus:border-blue-500
```

### 3.6 Disabled overlay

```
cursor-not-allowed bg-gray-50 opacity-60
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Default focus | keyboard focus | `focus:border-blue-500 focus:ring-2 focus:ring-blue-500` |
| Error | `error === true` | `border-red-400` |
| Error focus | error + focus | `focus:ring-red-500` |
| Disabled | `disabled === true` | `cursor-not-allowed bg-gray-50 opacity-60` |

---

## 5. Adornment width

The custom-symbol path uses 28px inline-start padding for backwards compatibility. Multi-character
symbols (for example `"USD"`) may overlap input text; ISO-aware consumers avoid this constraint by
using `currency`, whose full formatted value lives inside the input.

---

## 6. Open questions

1. **Size axis.** A `sm | md | lg` prop matching TextField would allow compact
   layouts.
2. **Custom-symbol placement.** `currencySymbol` deliberately preserves its legacy leading
   placement. Locale-correct placement is available through the ISO `currency` path.
3. **Focus ring offset.** `ring-offset-1` is used here but not in TextField.
   Confirm whether the offset is desirable or should be removed for consistency.
