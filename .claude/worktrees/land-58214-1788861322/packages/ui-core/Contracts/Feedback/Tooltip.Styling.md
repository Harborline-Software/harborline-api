# Tooltip — Styling Contract

- **Component:** Tooltip
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Interaction](./Tooltip.Interaction.md) · [Accessibility](./Tooltip.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-tooltip-bg` | Tooltip background (`foreground`) |
| `--sf-tooltip-text` | Tooltip text color (`background`) |
| `--sf-tooltip-shadow` | Tooltip shadow (`shadow-md`) |

---

## 2. Tailwind class recipes

### 2.1 Wrapper `<span>`

`relative inline-flex`

### 2.2 Tooltip bubble `<span>`

`absolute z-50 px-2 py-1 text-xs font-medium whitespace-nowrap bg-foreground text-background rounded-md shadow-md animate-in fade-in zoom-in-95 duration-100`

### 2.3 Positioning by side

| Side | Additional class |
|---|---|
| `top` (default) | `bottom-full left-1/2 -translate-x-1/2 mb-1.5` |
| `bottom` | `top-full left-1/2 -translate-x-1/2 mt-1.5` |
| `left` | `right-full top-1/2 -translate-y-1/2 mr-1.5` |
| `right` | `left-full top-1/2 -translate-y-1/2 ml-1.5` |

---

## 3. Do / Don't

### Do
- Use `bg-foreground text-background` (inverted theme colors) for the tooltip — creates natural contrast with the page background
- Keep `whitespace-nowrap` — tooltips are always single-line in M1

### Don't
- Don't put interactive content inside `content` — it's a string; use a Popover for rich interactive overlays
- Don't nest Tooltip inside an element with `overflow: hidden` without awareness of the clipping risk (G-TT1)
