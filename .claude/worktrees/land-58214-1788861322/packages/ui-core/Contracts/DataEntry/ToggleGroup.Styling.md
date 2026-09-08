# ToggleGroup — Styling Contract

- **Component:** ToggleGroup
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ToggleGroup.Semantic.md) · [Interaction](./ToggleGroup.Interaction.md) · [Accessibility](./ToggleGroup.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #A14 ToggleGroup (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

| Variant | Container classes |
|---|---|
| `outline` | `inline-flex -space-x-px` |
| `ghost` | `inline-flex` |

`-space-x-px` collapses adjacent borders in outline mode to 1px shared edges.

---

## 2. Button base (all options)

```
relative inline-flex items-center justify-center font-medium transition-colors
focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1
first:rounded-l-md last:rounded-r-md
```

`focus-visible:z-10` lifts the focused button above its neighbors so the ring isn't clipped.

---

## 3. Size

| `size` | Classes |
|---|---|
| `sm` | `h-7 px-2.5 text-xs` |
| `md` | `h-9 px-3 text-sm` |
| `lg` | `h-10 px-4 text-sm` |

---

## 4. Variant — unpressed state

| Variant | Classes |
|---|---|
| `outline` | `border border-gray-300 bg-white text-gray-700 hover:bg-gray-50` |
| `ghost` | `bg-transparent text-gray-600 hover:bg-gray-50` |

---

## 5. Variant — pressed state

| Variant | Classes |
|---|---|
| `outline` | `bg-gray-900 text-white border-gray-900` |
| `ghost` | `bg-gray-100 text-gray-900` |

---

## 6. Disabled state

`cursor-not-allowed opacity-50` — applied when `isDisabled` is true (group or option level).

---

## 7. Shape note

First and last buttons get `rounded-l-md` / `rounded-r-md` respectively, creating a pill-like connected group. Middle buttons have no rounding — flush with neighbors.
