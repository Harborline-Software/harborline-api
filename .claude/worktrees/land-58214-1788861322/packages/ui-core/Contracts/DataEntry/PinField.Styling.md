# PinField — Styling Contract

- **Component:** PinField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PinField.Semantic.md) · [Interaction](./PinField.Interaction.md) · [Accessibility](./PinField.Accessibility.md) · [Styling](./PinField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PinField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PinField renders `length` individual bordered input cells in a row. Cell
appearance varies by selection state (filled/empty), error, disabled, and
focus. No size axis — single fixed cell geometry (`h-12 w-10`).

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-pin-cell-h` | Cell height | `3rem` (h-12 = 48px) |
| `--sf-pin-cell-w` | Cell width | `2.5rem` (w-10 = 40px) |
| `--sf-pin-cell-bg` | Default background | `white` |
| `--sf-pin-cell-bg-filled` | Filled-cell background | `gray-50` |
| `--sf-pin-cell-border` | Default border | `gray-300` |
| `--sf-pin-cell-border-filled` | Filled-cell border | `gray-400` |
| `--sf-pin-cell-border-focus` | Focused border | `blue-500` |
| `--sf-pin-cell-ring-focus` | Focused ring | `blue-500` |
| `--sf-pin-cell-border-error` | Error border | `red-400` |
| `--sf-pin-cell-ring-focus-error` | Error focused ring | `red-500` |
| `--sf-pin-cell-disabled-opacity` | Disabled opacity | `0.6` |
| `--sf-pin-cell-fg` | Cell text | inherited (`gray-900` Tailwind default) |
| `--sf-pin-gap` | Gap between cells | `0.5rem` (gap-2) |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
flex gap-2
```

### 3.2 Cell input — base

```
h-12 w-10 rounded-md border text-center text-lg font-semibold tracking-widest
transition-colors focus:outline-none focus:ring-2 focus:ring-blue-500
focus:ring-offset-1
```

### 3.3 Cell — error state

```
border-red-400 focus:ring-red-500
```

### 3.4 Cell — default (no error)

```
border-gray-300 focus:border-blue-500
```

### 3.5 Cell — filled (has character, no error)

```
border-gray-400 bg-gray-50
```

Applied when `char && !error`.

### 3.6 Cell — disabled

```
cursor-not-allowed opacity-60
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Empty default | no char, no error | `border-gray-300` |
| Empty focused | focused, no char, no error | `focus:border-blue-500 focus:ring-2 focus:ring-blue-500` |
| Filled | has char, no error | `border-gray-400 bg-gray-50` |
| Error (all cells) | `error === true` | `border-red-400 focus:ring-red-500` |
| Disabled | `disabled === true` | `cursor-not-allowed opacity-60` |

State precedence: disabled > error > filled > focused > empty.

---

## 5. Typography

Cell inputs use `text-lg font-semibold tracking-widest`. The `tracking-widest`
is unusual for a single-character cell (each cell has only one character) — it
has no visible effect on a single character but is harmless. PAO may simplify
to `tracking-normal` or remove.

---

## 6. Cell gap

`gap-2` (8px) between cells. For longer PINs (`length > 6`), the total width
may exceed typical form widths. A `gap-1` or `gap-1.5` variant for dense
layouts may be needed.

---

## 7. Open questions

1. **Size axis.** `h-12 w-10` is the only cell size. Smaller (`h-10 w-8`) for
   inline OTP use cases would be useful.
2. **Filled state background.** `bg-gray-50` when filled provides a subtle
   visual confirmation. Confirm with design system whether this is intentional.
