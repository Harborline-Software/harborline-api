# Typography — Styling Contract

- **Component:** Typography
- **ADR 0017 family:** Typography
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** #143 Typography (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Token surface

Typography uses the Tailwind type scale. No custom CSS tokens — all styling is via Tailwind utility classes.

---

## 2. Variant class map

| Variant | Tailwind classes |
|---|---|
| `h1` | `scroll-m-20 text-4xl font-extrabold tracking-tight lg:text-5xl` |
| `h2` | `scroll-m-20 text-3xl font-semibold tracking-tight` |
| `h3` | `scroll-m-20 text-2xl font-semibold tracking-tight` |
| `h4` | `scroll-m-20 text-xl font-semibold tracking-tight` |
| `h5` | `scroll-m-20 text-lg font-semibold` |
| `h6` | `scroll-m-20 text-base font-semibold` |
| `body1` | `text-base leading-7` |
| `body2` | `text-sm leading-6` |
| `caption` | `text-xs text-muted-foreground` |
| `overline` | `text-xs font-semibold uppercase tracking-widest text-muted-foreground` |
| `label` | `text-sm font-medium leading-none` |

`scroll-m-20` provides scroll offset margin so anchored headings aren't hidden by fixed headers.

---

## 3. Do / Don't

### Do
- Use `className` to add `text-center`, color overrides, or `mt-*` spacing
- Use `as` to override the element when visual style and semantic element must differ

### Don't
- Don't duplicate the variant's font-size or font-weight classes in `className` — the variant already applies them
- Don't use `h1`/`h2`/`h3` variants for decorative large text — they create heading landmarks in the AT tree
