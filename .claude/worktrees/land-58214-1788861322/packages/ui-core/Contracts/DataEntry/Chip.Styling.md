# Chip — Styling Contract

- **Component:** Chip
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chip.Semantic.md) · [Interaction](./Chip.Interaction.md) · [Accessibility](./Chip.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Chip.tsx`
- **Catalog rows:** #25 Chip · #26 ChipList
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-chip-ring` | Selection ring (`ring-current/30`) |

Most visual differentiation comes from Tailwind semantic color tokens rather than dedicated chip-specific tokens.

---

## 2. Button base (always applied)

```
inline-flex items-center font-medium transition-colors
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1
disabled:opacity-50 disabled:cursor-not-allowed
```

---

## 3. Size

| Size | Classes |
|---|---|
| `small` | `h-6 px-2 text-xs gap-1` |
| `medium` | `h-8 px-3 text-sm gap-1.5` |
| `large` | `h-10 px-4 text-base gap-2` |

---

## 4. Rounded

| `rounded` | Classes |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

---

## 5. Fill mode × theme color

### 5.1 `solid` (default)

| `themeColor` | Classes |
|---|---|
| `base` | `bg-secondary text-secondary-foreground` |
| `primary` | `bg-primary text-primary-foreground` |
| `secondary` | `bg-secondary text-secondary-foreground` |
| `tertiary` | `bg-muted text-foreground` |
| `info` | `bg-blue-100 text-blue-800` |
| `success` | `bg-green-100 text-green-800` |
| `warning` | `bg-yellow-100 text-yellow-800` |
| `error` | `bg-red-100 text-red-800` |

### 5.2 `outline`

| `themeColor` | Classes |
|---|---|
| `base` | `border border-input bg-transparent text-foreground` |
| `primary` | `border border-primary bg-transparent text-primary` |
| `secondary` | `border border-secondary bg-transparent text-secondary-foreground` |
| `tertiary` | `border border-muted-foreground bg-transparent text-foreground` |
| `info` | `border border-blue-400 bg-transparent text-blue-700` |
| `success` | `border border-green-400 bg-transparent text-green-700` |
| `warning` | `border border-yellow-400 bg-transparent text-yellow-700` |
| `error` | `border border-red-400 bg-transparent text-red-700` |

### 5.3 `flat`

Flat removes the visible border and background; only text color from the outline palette remains.

| `themeColor` | Effective classes |
|---|---|
| `base` | `bg-transparent text-foreground` |
| `primary` | `bg-transparent text-primary` |
| `secondary` | `bg-transparent text-secondary-foreground` |
| `tertiary` | `bg-transparent text-foreground` |
| `info` | `bg-transparent text-blue-700` |
| `success` | `bg-transparent text-green-700` |
| `warning` | `bg-transparent text-yellow-700` |
| `error` | `bg-transparent text-red-700` |

*Implementation note: flat is derived by stripping the `border` width class from the outline value while retaining the border-color reference token and text color.*

---

## 6. Selection state

When `selected=true` (any fill mode, any theme color):
```
ring-2 ring-inset ring-current/30
```

This overlays a semi-transparent ring in the chip's current text color — works with all fill modes without needing per-theme overrides.

---

## 7. Internal element sizing

### Avatar image
| Size | Classes |
|---|---|
| `small` | `h-4 w-4 rounded-full object-cover shrink-0` |
| `medium` | `h-5 w-5 rounded-full object-cover shrink-0` |
| `large` | `h-7 w-7 rounded-full object-cover shrink-0` |

### Icon slot
`shrink-0` — no explicit size; inherits chip's `text-*` font size.

### Remove `×` control
`ml-0.5 opacity-60 hover:opacity-100 cursor-pointer`

---

## 8. ChipList container

`flex flex-wrap gap-1.5`

No additional visual styling — layout only.
