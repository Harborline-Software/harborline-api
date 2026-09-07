# Icon — Styling Contract

- **Component:** Icon
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. `Icon` base recipe

```
k-icon inline-flex items-center justify-center
```

Plus size and colour classes from the tables below.

---

## 2. Size recipes (shared by `Icon` and `SVGIcon`)

| `size` | Tailwind classes |
|---|---|
| `xsmall` | `h-3 w-3` |
| `small` | `h-4 w-4` |
| `medium` | `h-5 w-5` |
| `large` | `h-6 w-6` |
| `xlarge` | `h-8 w-8` |
| `xxlarge` | `h-10 w-10` |

---

## 3. Theme colour recipes (shared by `Icon` and `SVGIcon`)

| `themeColor` | Tailwind class |
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

The `text-foreground`, `text-primary`, `text-secondary-foreground`, `text-muted-foreground`, and `text-destructive` values are CSS custom property aliases from the design token system. The `text-blue-500`, `text-emerald-500`, `text-amber-500` values are direct Tailwind hues.

---

## 4. `SVGIcon` structure

```html
<span class="inline-flex items-center justify-center {sizeClass} {colorClass} {className}">
  <svg aria-hidden class="h-full w-full">…</svg>
</span>
```

The SVG fills its wrapper span via `h-full w-full`. Sizing is controlled on the wrapper.

---

## 5. Visual states

Icons are non-interactive. There is one visual state: the current colour + size.

Hover/focus/active states are owned by the parent interactive element (button, link, etc.). The parent may use CSS cascade to alter the icon's colour on hover (e.g., `group-hover:text-primary` if the icon wrapper has no explicit colour).

---

## 6. Dark mode

The semantic colour tokens (`text-foreground`, `text-primary`, etc.) respect dark mode automatically via the design token system. Direct hues (`text-blue-500`, `text-emerald-500`, etc.) do NOT invert — provider themes supply `dark:` variants.

---

## 7. Token surface

Icons use the shared design token aliases (`text-foreground`, `text-primary`, etc.) for semantic colours. No `--sf-icon-*` tokens are defined in the reference implementation. If per-component token isolation is needed, propose `--sf-icon-color-{role}` tokens mapped to the semantic aliases.
