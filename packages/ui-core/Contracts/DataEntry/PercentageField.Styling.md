# PercentageField — Styling Contract

- **Component:** PercentageField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PercentageField.Semantic.md) · [Interaction](./PercentageField.Interaction.md) · [Accessibility](./PercentageField.Accessibility.md) · [Styling](./PercentageField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PercentageField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PercentageField uses the same unified bordered wrapper as NumericInput but
without increment/decrement buttons. It has a fixed trailing `%` adornment.
Token surface mirrors NumericInput with minor adjustments.

---

## 2. Token surface

PercentageField shares the `--sf-numeric-*` token family with NumericInput.
No new tokens are introduced.

| Token | Usage |
|---|---|
| `--sf-numeric-bg` | Wrapper background |
| `--sf-numeric-border` | Default border |
| `--sf-numeric-border-focus` | Focused border |
| `--sf-numeric-ring-focus` | Focused ring |
| `--sf-numeric-border-error` | Error border |
| `--sf-numeric-ring-focus-error` | Error focused ring |
| `--sf-numeric-bg-disabled` | Disabled background |
| `--sf-numeric-disabled-opacity` | Disabled opacity |
| `--sf-numeric-adornment-bg` | `%` suffix background |
| `--sf-numeric-adornment-border` | `%` suffix border |
| `--sf-numeric-adornment-fg` | `%` suffix text |
| `--sf-numeric-label-fg` | Label text |
| `--sf-numeric-hint-fg` | Hint text |
| `--sf-numeric-error-fg` | Error text |

---

## 3. Tailwind class recipes

### 3.1 Outer container

```
flex flex-col gap-1
```

### 3.2 Label

```
text-sm font-medium text-gray-700
```

### 3.3 Inner bordered wrapper

```
flex items-center rounded-md border bg-white overflow-hidden
focus-within:ring-2 focus-within:ring-blue-500
```

Default: `border-gray-300 focus-within:border-blue-500`
Error: `border-red-400 focus-within:ring-red-500`
Disabled: `opacity-50 pointer-events-none bg-gray-50`

### 3.4 Input

```
flex-1 min-w-0 bg-transparent px-3 py-2 text-sm outline-none
[appearance:textfield]
[&::-webkit-outer-spin-button]:appearance-none
[&::-webkit-inner-spin-button]:appearance-none
```

### 3.5 Suffix `%` adornment

```
shrink-0 border-l border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500
```

### 3.6 Hint

```
text-xs text-gray-500
```

### 3.7 Error

```
text-xs text-red-600
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Focus-within | input focused | `focus-within:border-blue-500 focus-within:ring-2 focus-within:ring-blue-500` |
| Error | `error` truthy | `border-red-400 focus-within:ring-red-500` |
| Disabled | `disabled` | `opacity-50 pointer-events-none bg-gray-50` |

---

## 5. Open questions

1. **Size axis.** No `size` prop; single density. Align with NumericInput if
   a size prop is added to either.
2. **Suffix on the left.** Some number-entry patterns position the unit on the
   left (`"% rate"`). A `suffixPosition` prop may be needed.
