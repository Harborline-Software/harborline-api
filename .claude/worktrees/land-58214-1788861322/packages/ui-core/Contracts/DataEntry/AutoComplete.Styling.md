# AutoComplete — Styling Contract

- **Component:** AutoComplete
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AutoComplete.Semantic.md) · [Interaction](./AutoComplete.Interaction.md) · [Accessibility](./AutoComplete.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AutoComplete.tsx`
- **Catalog row:** #7 AutoComplete (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + `className` passthrough.

---

## 2. Input container

`flex items-center px-3` + fillMode + rounded + size classes.

| `fillMode` | Classes |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

| `rounded` | Classes |
|---|---|
| `small` | `rounded` |
| `medium` (default) | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

| `size` | Classes |
|---|---|
| `small` | `h-7 text-sm` |
| `medium` (default) | `h-9 text-sm` |
| `large` | `h-11 text-base` |

---

## 3. Inner input

`flex-1 outline-none bg-transparent min-w-0`

---

## 4. Loading indicator

`text-muted-foreground text-xs ml-2` — renders `⟳` text character.

---

## 5. Suggestion list

`absolute z-50 w-full mt-1 bg-popover border border-border rounded-md shadow-md max-h-48 overflow-y-auto`

---

## 6. Suggestion item

Base: `px-3 py-1.5 cursor-pointer text-sm`

| State | Classes |
|---|---|
| Active (`i === activeIdx`) | `bg-accent text-accent-foreground` |
| Inactive | `hover:bg-muted` |
