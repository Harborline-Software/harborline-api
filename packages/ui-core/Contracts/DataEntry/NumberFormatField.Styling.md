# NumberFormatField — Styling Contract

- **Component:** NumberFormatField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberFormatField.Semantic.md) · [Interaction](./NumberFormatField.Interaction.md) · [Accessibility](./NumberFormatField.Accessibility.md) · [Styling](./NumberFormatField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberFormatField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumberFormatField wraps its `<input>` in a flex container that renders the
focus ring and border. The inner input is transparent-background (`bg-transparent`)
with no outline. Prefix/suffix adornments live inside the same flex wrapper.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-numformat-bg` | Wrapper background | `white` |
| `--sf-numformat-border` | Default wrapper border | `gray-300` |
| `--sf-numformat-border-focus` | Focused wrapper border | `blue-500` |
| `--sf-numformat-ring-focus` | Focused ring | `blue-500/20` (20% opacity ring) |
| `--sf-numformat-bg-disabled` | Disabled wrapper background | `gray-50` |
| `--sf-numformat-disabled-opacity` | Disabled opacity | `0.5` |
| `--sf-numformat-fg` | Input text | `gray-900` |
| `--sf-numformat-placeholder-fg` | Placeholder text | `gray-400` |
| `--sf-numformat-adornment-fg` | Prefix/suffix text | `gray-400` |
| `--sf-numformat-radius` | Wrapper border radius | `0.75rem` (rounded-xl) |

---

## 3. Tailwind class recipes

### 3.1 Wrapper (idle)

```
flex items-center gap-1 rounded-xl border px-3 py-2 bg-white border-gray-300
```

### 3.2 Wrapper (focused)

```
border-blue-500 ring-2 ring-blue-500/20
```

Applied via `focused` state boolean in JS (not CSS `:focus-within`).

### 3.3 Wrapper (disabled)

```
opacity-50 bg-gray-50
```

### 3.4 Input

```
flex-1 min-w-0 bg-transparent text-sm text-gray-900 outline-none
placeholder-gray-400 disabled:cursor-not-allowed
```

### 3.5 Prefix / suffix

```
text-sm text-gray-400 shrink-0
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | idle, not focused | `border-gray-300` |
| Focused | input focused | `border-blue-500 ring-2 ring-blue-500/20` |
| Disabled | `disabled === true` | `opacity-50 bg-gray-50` |
| Error | `error === true` | `border-destructive`; focused error uses `ring-destructive/20` |

The error state uses shared semantic tokens and introduces no raw color.

---

## 5. Border radius

`rounded-xl` (12px radius) is used — notably larger than the standard
`rounded-md` (6px) used by TextField, CurrencyField, and other inputs. This
is a visual differentiation in the current implementation.

PAO should confirm whether this deviation from the shared `--sf-input-radius`
token is intentional or should be normalized to `rounded-md`.

---

## 6. Ring opacity

`ring-blue-500/20` uses a 20% opacity ring, creating a soft glow effect rather
than a solid ring. This differs from the `ring-blue-500` (full opacity) used in
TextField. Accessibility: a semi-transparent ring may fail WCAG 2.4.13 if the
effective contrast is below threshold on non-white backgrounds. PAO should
verify.

---

## 7. Open questions

1. **`rounded-xl` vs `rounded-md`.** Should NumberFormatField align with the
   shared input border-radius token?
2. **Ring opacity.** `ring-blue-500/20` vs `ring-blue-500` — confirm WCAG
   compliance on all host backgrounds.
