# NumberStepper — Styling Contract

- **Component:** NumberStepper
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberStepper.Semantic.md) · [Interaction](./NumberStepper.Interaction.md) · [Accessibility](./NumberStepper.Accessibility.md) · [Styling](./NumberStepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberStepper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

NumberStepper renders three separate bordered elements in a row: decrement
button, number input, increment button. Unlike NumericInput (which uses a
unified bordered wrapper), each element has its own border. Three size variants
are supported.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-stepper-bg` | Button background | `white` |
| `--sf-stepper-fg` | Button text | `gray-700` |
| `--sf-stepper-border` | Button/input border | `gray-300` |
| `--sf-stepper-bg-hover` | Button hover fill | `gray-50` |
| `--sf-stepper-bg-active` | Button active (pressed) fill | `gray-100` |
| `--sf-stepper-disabled-opacity` | Disabled opacity | `0.5` |
| `--sf-stepper-focus-ring` | Focus ring colour | `blue-500` |
| `--sf-stepper-input-fg` | Input text colour | `gray-900` |
| `--sf-stepper-input-bg-disabled` | Disabled input background | `gray-50` |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
inline-flex items-center gap-1
```

### 3.2 Decrement / Increment buttons (base)

```
flex items-center justify-center rounded-md border border-gray-300 bg-white
font-medium text-gray-700 hover:bg-gray-50 active:bg-gray-100 transition-colors
disabled:cursor-not-allowed disabled:opacity-50
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

### 3.3 Button sizes

| Size | Classes |
|---|---|
| `sm` | `h-7 w-7 text-sm` |
| `md` | `h-9 w-9 text-base` |
| `lg` | `h-11 w-11 text-lg` |

### 3.4 Input (base)

```
rounded-md border border-gray-300 text-center font-medium text-gray-900
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
disabled:cursor-not-allowed disabled:opacity-50 disabled:bg-gray-50
[appearance:textfield]
[&::-webkit-outer-spin-button]:appearance-none
[&::-webkit-inner-spin-button]:appearance-none
```

### 3.5 Input sizes

| Size | Classes |
|---|---|
| `sm` | `h-7 w-12 text-sm` |
| `md` | `h-9 w-14 text-base` |
| `lg` | `h-11 w-16 text-lg` |

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300 bg-white` |
| Button hover | mouse over button | `hover:bg-gray-50` |
| Button active | mouse pressed | `active:bg-gray-100` |
| Focus-visible | keyboard focus | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| Disabled | `disabled === true` or at boundary | `disabled:opacity-50 disabled:cursor-not-allowed` |
| Input disabled | `disabled === true` | `disabled:bg-gray-50` additionally |

No error visual state defined.

---

## 5. Open questions

1. **Error state.** When `error` prop is added, define border-red + ring-red
   on the input (buttons likely stay neutral).
2. **Unified border.** Some designs prefer a single bordered wrapper with the
   buttons inside (like NumericInput). Compare with NumericInput to decide if
   NumberStepper should consolidate its visual approach.
3. **Size `xs` / `xl`.** For dense data grids, an `xs` size smaller than `sm`
   may be useful.
