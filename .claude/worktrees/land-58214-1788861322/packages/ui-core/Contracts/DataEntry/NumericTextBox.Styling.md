# NumericTextBox — Styling Contract

- **Component:** NumericTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericTextBox.Semantic.md) · [Interaction](./NumericTextBox.Interaction.md) · [Accessibility](./NumericTextBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericTextBox.tsx`
- **Catalog row:** #90 NumericTextBox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`flex items-center overflow-hidden` + fillMode + rounded + size + disabled opacity + `className` passthrough.

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

Disabled: `opacity-50 cursor-not-allowed`

---

## 2. Input element

`flex-1 min-w-0 px-3 outline-none bg-transparent`

---

## 3. Spinner container

`flex flex-col border-l border-input h-full`

---

## 4. Spinner buttons

Base: `flex-1 px-1.5 hover:bg-muted disabled:opacity-30 text-xs leading-none`

Decrement button adds: `border-t border-input`

Content: `▲` (increment), `▼` (decrement).
