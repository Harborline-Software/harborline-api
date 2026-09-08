# TextArea — Styling Contract

- **Component:** TextArea
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TextArea.Semantic.md) · [Interaction](./TextArea.Interaction.md) · [Accessibility](./TextArea.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TextArea.tsx`
- **Catalog row:** #133 TextArea (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper div

`relative` — contains textarea + optional counter.

---

## 2. Textarea classes

Base: `w-full outline-none transition-colors placeholder:text-muted-foreground focus:ring-2 focus:ring-ring focus:ring-offset-0 disabled:cursor-not-allowed disabled:opacity-50`

**fillMode classes:**

| fillMode | Class |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-0 border-b border-input rounded-none` |

**rounded classes:**

| rounded | Class |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-lg` (no true full round for textarea) |

**size classes:**

| size | Class |
|---|---|
| `small` | `text-sm px-2 py-1` |
| `medium` | `text-sm px-3 py-2` |
| `large` | `text-base px-4 py-3` |

**resize classes:**

| resize | Class |
|---|---|
| `none` | `resize-none` |
| `vertical` | `resize-y` |
| `horizontal` | `resize-x` |
| `both` | `resize` |

Invalid: `border-destructive`

`className` passthrough merges via `cn()`.

---

## 3. Counter span

`absolute bottom-1 right-2 text-xs text-muted-foreground pointer-events-none`

Content: `"{len}/{maxLength}"` when maxLength set, `"{len}"` otherwise.

> **M1 note:** Uses design tokens `border-input`, `ring-ring`, `text-muted-foreground` — not hardcoded colors.
