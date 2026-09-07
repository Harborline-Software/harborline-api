# Tooltip — Styling Contract

- **Component:** Tooltip
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Interaction](./Tooltip.Interaction.md) · [Accessibility](./Tooltip.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper

`relative inline-block` — inline so the tooltip positions around the trigger without breaking flow.

---

## 2. Tooltip label

Base: `absolute z-50 px-2 py-1 text-xs font-medium whitespace-nowrap bg-foreground text-background rounded-md shadow-md animate-in fade-in zoom-in-95 duration-100`

---

## 3. Side-position classes

| `side` | Classes |
|---|---|
| `top` (default) | `bottom-full left-1/2 -translate-x-1/2 mb-1.5` |
| `bottom` | `top-full left-1/2 -translate-x-1/2 mt-1.5` |
| `left` | `right-full top-1/2 -translate-y-1/2 mr-1.5` |
| `right` | `left-full top-1/2 -translate-y-1/2 ml-1.5` |

---

## 4. Animation

`animate-in fade-in zoom-in-95 duration-100` — enter-only animation via `tailwindcss-animate`. No exit animation (conditionally unmounted on hide).

---

## 5. Color tokens

| Token | Usage |
|---|---|
| `bg-foreground` | Tooltip background (inverted surface) |
| `text-background` | Tooltip text (inverted text) |
| `shadow-md` | Drop shadow |
