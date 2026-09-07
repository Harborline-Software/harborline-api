# Typography — Styling Contract

- **Component:** Typography
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Variant class map

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

`scroll-m-20` on headings accounts for sticky header offset when headings are anchor scroll targets.

## 2. className merge

Host-provided `className` is merged with the variant class via `cn(variantClass[variant], className)`. Host classes are appended and can override variant defaults.

## 3. CSS variables

| Variable | Used by |
|---|---|
| `--muted-foreground` | `caption`, `overline` — text colour for secondary/auxiliary text |

Heading and body variants use Tailwind's base colour (no CSS variable reference).

## 4. Responsive variant

`h1` applies `lg:text-5xl` — increases from `4xl` to `5xl` on large viewports. No other responsive classes are applied by default.

## 5. No spacing utilities

Typography does not apply `mt-*`, `mb-*`, or `space-y-*` by default. Vertical spacing between typographic elements is the host layout's responsibility (a common pattern: `space-y-4` on the container).
