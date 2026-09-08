# SplitButton — Styling Contract

- **Component:** SplitButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SplitButton.Semantic.md) · [Interaction](./SplitButton.Interaction.md) · [Accessibility](./SplitButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/SplitButton.tsx`
- **Catalog row:** #124 SplitButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`relative inline-flex` — two buttons are placed side-by-side with no gap.

---

## 2. Primary button

`inline-flex items-center justify-center font-medium transition-colors`

Size height + padding (shared with caret):

| Size | Primary classes |
|---|---|
| `sm` | `h-7 text-xs px-2.5` |
| `md` | `h-9 text-sm px-3` |
| `lg` | `h-11 text-base px-4` |

Left-side rounding: `rounded-l` (right edge is flat, adjoins caret).

---

## 3. Caret button

`inline-flex items-center justify-center border-l transition-colors`
Right-side rounding: `rounded-r`

| Size | Caret width |
|---|---|
| `sm` | `w-7` |
| `md` | `w-8` |
| `lg` | `w-9` |

---

## 4. Variant colors

> **M1 deviation:** Colors are hardcoded values, not Harborline design tokens. Future work should align to the token system.

| Variant | Primary + Caret base | Hover |
|---|---|---|
| `primary` | `bg-blue-600 text-white` | `hover:bg-blue-700` |
| `default` | `bg-gray-300 text-gray-800` | `hover:bg-gray-50` |

Border between primary and caret (variant-specific):
- `primary`: `border-blue-700`
- `default`: `border-gray-400`

---

## 5. Loading state

Primary button shows: `<svg class="animate-spin h-4 w-4">` in place of label text. No style changes to the button shell.

---

## 6. Disabled state

Both buttons: `opacity-50 cursor-not-allowed pointer-events-none` (applied when `disabled=true` or `loading=true`).

---

## 7. Dropdown menu

`absolute z-50 mt-1 min-w-full rounded border border-border bg-popover shadow-md left-0`

Items: `flex items-center w-full px-3 py-2 text-sm hover:bg-accent cursor-pointer`
Disabled items: `opacity-50 pointer-events-none`
