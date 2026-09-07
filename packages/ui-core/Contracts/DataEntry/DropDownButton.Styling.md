# DropDownButton — Styling Contract

- **Component:** DropDownButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownButton.Semantic.md) · [Interaction](./DropDownButton.Interaction.md) · [Accessibility](./DropDownButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DropDownButton.tsx`
- **Catalog row:** #47 DropDownButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`relative inline-flex`

---

## 2. Trigger button — base

`inline-flex items-center rounded font-medium shadow-sm transition-colors`

---

## 3. Size tokens

| Size | Classes |
|---|---|
| `small` | `h-7 px-2.5 text-xs gap-1` |
| `medium` | `h-9 px-3 text-sm gap-1.5` |
| `large` | `h-11 px-4 text-base gap-2` |

---

## 4. Theme color classes (fillMode=solid)

| themeColor | Classes |
|---|---|
| `primary` | `bg-primary text-primary-foreground hover:bg-primary/90` |
| `secondary` | `bg-secondary text-secondary-foreground hover:bg-secondary/80` |
| `base` | `bg-muted text-foreground hover:bg-muted/80` |
| `tertiary` | `bg-accent text-accent-foreground hover:bg-accent/80` |
| `info` | `bg-blue-500 text-white hover:bg-blue-600` |
| `success` | `bg-green-500 text-white hover:bg-green-600` |
| `warning` | `bg-yellow-500 text-white hover:bg-yellow-600` |
| `error` | `bg-destructive text-destructive-foreground hover:bg-destructive/90` |

---

## 5. Fill mode classes (non-solid)

| fillMode | Classes |
|---|---|
| `flat` | `bg-transparent hover:bg-accent shadow-none` |
| `outline` | `bg-transparent border border-input hover:bg-accent` |
| `link` | `bg-transparent underline-offset-4 hover:underline shadow-none p-0 h-auto` |
| `clear` | `bg-transparent hover:bg-accent shadow-none border-0` |

---

## 6. Dropdown menu

`absolute z-50 mt-1 min-w-full rounded border border-border bg-popover shadow-md`

Alignment:
- `popupAlign='left'`: `left-0`
- `popupAlign='right'`: `right-0`

---

## 7. Menu items

`flex items-center gap-2 w-full px-3 py-2 text-sm hover:bg-accent cursor-pointer`
Disabled: `opacity-50 pointer-events-none`

---

## 8. Disabled state

Trigger: `opacity-50 pointer-events-none`
