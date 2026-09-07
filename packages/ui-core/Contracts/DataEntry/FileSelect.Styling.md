# FileSelect — Styling Contract

- **Component:** FileSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileSelect.Semantic.md) · [Interaction](./FileSelect.Interaction.md) · [Accessibility](./FileSelect.Accessibility.md) · [Styling](./FileSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base button recipe

```
inline-flex items-center gap-1.5 rounded-md border border-input bg-background font-medium
hover:bg-accent transition-colors
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
disabled:pointer-events-none disabled:opacity-50
```

---

## 2. Size axis

| Size | Height | Padding | Font |
|---|---|---|---|
| `small` | `h-7` | `px-2.5` | `text-xs` |
| `medium` (default) | `h-9` | `px-3` | `text-sm` |
| `large` | `h-11` | `px-4` | `text-base` |

---

## 3. Visual state inventory

| State | Recipe |
|---|---|
| default | `border-input bg-background` |
| hover | `hover:bg-accent` |
| focus-visible | `focus-visible:ring-2 focus-visible:ring-ring` |
| disabled | `disabled:pointer-events-none disabled:opacity-50` |

---

## 4. Semantic color tokens consumed

| Token class | Meaning |
|---|---|
| `border-input` | Input/button border |
| `bg-background` | Component background (typically white) |
| `hover:bg-accent` | Hover fill |
| `focus-visible:ring-ring` | Focus ring color |
