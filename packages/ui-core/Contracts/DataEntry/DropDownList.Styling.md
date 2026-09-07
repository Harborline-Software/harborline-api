# DropDownList — Styling Contract

- **Component:** DropDownList
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownList.Semantic.md) · [Interaction](./DropDownList.Interaction.md) · [Accessibility](./DropDownList.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownList.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`relative flex items-center` + fillMode + rounded + size + `className` passthrough.

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

## 2. Native select (invisible)

`absolute inset-0 w-full opacity-0 cursor-pointer disabled:cursor-not-allowed`

---

## 3. Display text span

`pl-3 flex-1 truncate text-left pointer-events-none`

Placeholder text: `text-muted-foreground`

---

## 4. Chevron span

`pr-3 text-muted-foreground pointer-events-none shrink-0`

Content: `⟳` when loading, `▾` otherwise.
