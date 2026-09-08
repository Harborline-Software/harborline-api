# MaskedTextBox — Styling Contract

- **Component:** MaskedTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedTextBox.Semantic.md) · [Interaction](./MaskedTextBox.Interaction.md) · [Accessibility](./MaskedTextBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MaskedTextBox.tsx`
- **Catalog row:** #82 MaskedTextBox (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base (always applied)

```
flex w-full outline-none transition-colors
placeholder:text-muted-foreground
focus:ring-2 focus:ring-ring
disabled:cursor-not-allowed disabled:opacity-50
```

---

## 2. Size

| `size` | Classes |
|---|---|
| `small` | `h-7 text-sm px-2` |
| `medium` | `h-9 text-sm px-3` |
| `large` | `h-11 text-base px-4` |

---

## 3. Fill mode

| `fillMode` | Classes |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

---

## 4. Rounded

| `rounded` | Classes |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |
