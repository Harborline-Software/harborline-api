# NumericInput — Styling Contract

- **Component:** NumericInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericInput.Semantic.md) · [Interaction](./NumericInput.Interaction.md) · [Accessibility](./NumericInput.Accessibility.md) · [Styling](./NumericInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumericInput uses a **unified bordered wrapper** pattern: one border around the
decrement button, input, and increment button as a group. Prefix and suffix
adornments have bordered left/right edges inside the same wrapper. Focus ring
fires on the wrapper via `focus-within`.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-numeric-bg` | Wrapper background | `white` |
| `--sf-numeric-border` | Default wrapper border | `gray-300` |
| `--sf-numeric-border-focus` | Focused wrapper border | `blue-500` |
| `--sf-numeric-ring-focus` | Focused wrapper ring | `blue-500` |
| `--sf-numeric-border-error` | Error border | `red-400` |
| `--sf-numeric-ring-focus-error` | Error focused ring | `red-500` |
| `--sf-numeric-bg-disabled` | Disabled background | `gray-50` |
| `--sf-numeric-disabled-opacity` | Disabled opacity | `0.5` |
| `--sf-numeric-btn-fg` | Button icon text | `gray-500` |
| `--sf-numeric-btn-bg-hover` | Button hover | `gray-100` |
| `--sf-numeric-btn-disabled-opacity` | Disabled button opacity | `0.4` |
| `--sf-numeric-adornment-bg` | Prefix/suffix background | `gray-50` |
| `--sf-numeric-adornment-border` | Prefix/suffix inner border | `gray-200` |
| `--sf-numeric-adornment-fg` | Prefix/suffix text | `gray-500` |
| `--sf-numeric-label-fg` | Label text | `gray-700` |
| `--sf-numeric-hint-fg` | Hint text | `gray-500` |
| `--sf-numeric-error-fg` | Error text | `red-600` |

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

Default border:

```
border-gray-300 focus-within:border-blue-500
```

Error border:

```
border-red-400 focus-within:ring-red-500
```

Disabled overlay:

```
opacity-50 pointer-events-none bg-gray-50
```

### 3.4 Prefix / Suffix adornments

```
shrink-0 px-3 py-2 text-sm text-gray-500 bg-gray-50
```

Prefix: `border-r border-gray-200`. Suffix: `border-l border-gray-200`.

### 3.5 Decrement / Increment buttons

```
px-2 py-1 text-gray-500 hover:bg-gray-100 disabled:opacity-40 text-lg leading-none
```

No border (inside the unified wrapper).

### 3.6 Input

```
w-full min-w-0 flex-1 bg-transparent py-2 text-center text-sm outline-none
[appearance:textfield]
[&::-webkit-outer-spin-button]:appearance-none
[&::-webkit-inner-spin-button]:appearance-none
```

### 3.7 Hint

```
text-xs text-gray-500
```

### 3.8 Error message

```
text-xs text-red-600
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Focus-within | any child focused | `focus-within:border-blue-500 focus-within:ring-2 focus-within:ring-blue-500` |
| Error | `error` truthy | `border-red-400 focus-within:ring-red-500` |
| Disabled | `disabled` prop | `opacity-50 pointer-events-none bg-gray-50` |

---

## 5. Open questions

1. **Size axis.** No `size` prop; single `md`-equivalent density.
2. **Spin-button suppression.** Native spin buttons are suppressed via
   `[appearance:textfield]`. Confirm this is the intended UX given the
   dedicated decrement/increment buttons.
