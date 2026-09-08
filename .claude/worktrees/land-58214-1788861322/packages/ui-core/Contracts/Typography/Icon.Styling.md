# Icon — Styling Contract

- **Component:** Icon
- **ADR 0017 family:** Typography
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** #70 Icon (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

Icon uses the theme color system. All themeColor values map to Tailwind text color utilities:

| `themeColor` | Class |
|---|---|
| `base` | `text-foreground` |
| `primary` | `text-primary` |
| `secondary` | `text-secondary-foreground` |
| `tertiary` | `text-muted-foreground` |
| `info` | `text-blue-500` |
| `success` | `text-emerald-500` |
| `warning` | `text-amber-500` |
| `error` | `text-destructive` |
| `inherit` | `text-inherit` |

---

## 2. Tailwind class recipes

### 2.1 Icon wrapper `<span>`

`k-icon inline-flex items-center justify-center` + size class + color class

Size classes:

| Size | Class |
|---|---|
| `xsmall` | `h-3 w-3` |
| `small` | `h-4 w-4` |
| `medium` | `h-5 w-5` |
| `large` | `h-6 w-6` |
| `xlarge` | `h-8 w-8` |
| `xxlarge` | `h-10 w-10` |

### 2.2 SVGIcon wrapper `<span>`

`inline-flex items-center justify-center` + size class + color class

Inner `<svg>`: `h-full w-full` (fills the wrapper exactly)

---

## 3. Do / Don't

### Do
- Use `themeColor="inherit"` when the icon should match surrounding text color
- Use `SVGIcon` for custom SVG assets; use `Icon` for glyph-font icons

### Don't
- Don't manually add size classes to `className` — use the `size` prop
