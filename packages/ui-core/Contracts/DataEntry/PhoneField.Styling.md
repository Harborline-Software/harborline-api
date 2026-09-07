# PhoneField — Styling Contract

- **Component:** PhoneField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PhoneField.Semantic.md) · [Interaction](./PhoneField.Interaction.md) · [Accessibility](./PhoneField.Accessibility.md) · [Styling](./PhoneField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PhoneField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PhoneField renders a text input with a leading `+1` adornment overlay (not a
bordered adornment — it is absolutely positioned inside a `relative` wrapper).
It shares the `--sf-input-*` token surface with TextField. No size axis.

---

## 2. Token surface

PhoneField reuses `--sf-input-*`. Additional token for the prefix adornment:

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-input-bg` | Input background | `white` |
| `--sf-input-fg` | Input text | implied by Tailwind default |
| `--sf-input-placeholder-fg` | Placeholder | `gray-400` |
| `--sf-input-border` | Default border | `gray-300` |
| `--sf-input-border-focus` | Focused border | `blue-500` |
| `--sf-input-ring-focus` | Focused ring | `blue-500` |
| `--sf-input-border-error` | Error border | `red-400` |
| `--sf-input-ring-focus-error` | Error focused ring | `red-500` |
| `--sf-phone-prefix-fg` | `+1` prefix colour | `gray-400` |

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

### 3.3 Input wrapper (`relative`)

```
relative
```

### 3.4 `+1` prefix span

```
absolute inset-y-0 left-3 flex items-center pointer-events-none text-gray-400
text-sm select-none
```

### 3.5 Input

```
block w-full rounded-md border bg-white pl-9 pr-3 py-2 text-sm shadow-sm
placeholder:text-gray-400
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500
disabled:bg-gray-50 disabled:text-gray-500 disabled:cursor-not-allowed
```

`pl-9` reserves space for the `+1` prefix.

### 3.6 Error overlay

```
border-red-400 focus:ring-red-500 focus:border-red-500
```

### 3.7 Default (non-error) overlay

```
border-gray-300
```

### 3.8 Hint

```
text-xs text-gray-500
```

### 3.9 Error message

```
text-xs text-red-600
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Focused | keyboard focus | `focus:ring-2 focus:ring-blue-500 focus:border-blue-500` |
| Error | `error` truthy | `border-red-400` |
| Error focused | error + focus | `focus:ring-red-500 focus:border-red-500` |
| Disabled | `disabled` prop | `disabled:bg-gray-50 disabled:text-gray-500 disabled:cursor-not-allowed` |

---

## 5. Shadow

`shadow-sm` is applied to the input. This is a minor visual differentiation
from TextField (which uses no shadow). PAO should confirm whether the shadow
aligns with the design system's input token surface.

---

## 6. Open questions

1. **`shadow-sm` consistency.** Remove or add to the shared `--sf-input-*`
   token surface for consistency.
2. **Size axis.** No `size` prop; single density.
3. **International support.** A future `countryCode` prop would require a
   country-code selector UI — significant scope increase.
