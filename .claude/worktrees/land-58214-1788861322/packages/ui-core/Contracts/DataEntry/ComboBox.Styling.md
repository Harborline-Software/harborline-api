# ComboBox — Styling Contract

- **Component:** ComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ComboBox.Semantic.md) · [Interaction](./ComboBox.Interaction.md) · [Accessibility](./ComboBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ComboBox.tsx`
- **Catalog row:** #32 ComboBox (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + `className` passthrough.

---

## 2. Input container

`flex items-center px-3 gap-1` + fillMode + rounded + size classes.

**fillMode classes:**

| fillMode | Class |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

**rounded classes:**

| rounded | Class |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

**size classes:**

| size | Class |
|---|---|
| `small` | `h-7 text-sm` |
| `medium` | `h-9 text-sm` |
| `large` | `h-11 text-base` |

---

## 3. Input element

`flex-1 outline-none bg-transparent min-w-0`

---

## 4. Toggle button

`text-muted-foreground shrink-0` · `tabIndex={-1}`

Content: `⟳` when loading, `▴` when open, `▾` when closed.

---

## 5. Dropdown list

`absolute z-50 w-full mt-1 bg-popover border border-border rounded-md shadow-md max-h-48 overflow-y-auto`

---

## 6. Option items

Base: `px-3 py-1.5 cursor-pointer text-sm`

Active (keyboard): `bg-accent text-accent-foreground`

Selected: `font-medium`

Disabled: `opacity-50 cursor-not-allowed`

Hover (non-disabled): `hover:bg-muted`
