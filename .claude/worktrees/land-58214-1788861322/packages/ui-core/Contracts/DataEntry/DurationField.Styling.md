# DurationField — Styling Contract

- **Component:** DurationField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DurationField.Semantic.md) · [Interaction](./DurationField.Interaction.md) · [Accessibility](./DurationField.Accessibility.md) · [Styling](./DurationField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DurationField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

DurationField renders 2–3 compact number inputs with a monospace font, colon
separators, and small unit labels below each segment. There is no size axis —
one fixed density.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-duration-input-bg` | Segment input background | `white` |
| `--sf-duration-input-fg` | Segment input text | `gray-900` |
| `--sf-duration-input-border` | Segment input border | `gray-300` |
| `--sf-duration-input-bg-disabled` | Disabled background | `gray-50` |
| `--sf-duration-input-opacity-disabled` | Disabled opacity | `0.5` |
| `--sf-duration-focus-ring` | Focus ring colour | `blue-500` |
| `--sf-duration-separator-fg` | Colon separator colour | `gray-400` |
| `--sf-duration-unit-label-fg` | Unit label text (`hr`, `min`, `sec`) | `gray-400` |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
flex items-center gap-1
```

### 3.2 Segment column wrapper

```
flex flex-col items-center gap-0.5
```

### 3.3 Segment input (base)

```
w-14 rounded-md border border-gray-300 px-2 py-1.5 text-sm text-center font-mono
text-gray-900 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
disabled:bg-gray-50 disabled:opacity-50
```

Minutes segment in `'ms'` format uses `w-16` (wider to accommodate larger minute
values up to 9999).

### 3.4 Unit label

```
text-xs text-gray-400
```

Rendered as a `<span>` below each segment: `"hr"`, `"min"`, `"sec"`.

### 3.5 Colon separator

```
text-gray-400 font-mono select-none
```

---

## 4. Visual state inventory

| State | Condition | Recipe |
|---|---|---|
| Default | enabled, unfocused | `border-gray-300` |
| Focus-visible | keyboard focus | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| Disabled | `disabled === true` | `disabled:bg-gray-50 disabled:opacity-50` |

No error visual state defined (no `error` prop).

---

## 5. Monospace font

Segment inputs use `font-mono` to ensure digit characters align predictably and
the zero-padded display (`"01"`, `"09"`) has consistent visual weight.

---

## 6. Open questions

1. **Size axis.** A `size` prop would allow compact (sm) or touch-friendly (lg)
   segment inputs.
2. **Error visual state.** When an `error` prop is added, the segment border and
   focus ring should shift to `border-red-400` / `ring-red-500`.
3. **Native spin-button suppression.** Consider suppressing native spin buttons
   (via `[appearance:textfield]`) for a cleaner look matching NumericInput.
