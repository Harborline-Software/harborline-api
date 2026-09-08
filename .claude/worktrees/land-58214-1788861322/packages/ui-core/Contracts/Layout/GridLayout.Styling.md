# GridLayout — Styling Contract

- **Component:** GridLayout
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GridLayout.Semantic.md) · [Interaction](./GridLayout.Interaction.md) · [Accessibility](./GridLayout.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/GridLayout.tsx`
- **Catalog row:** #67 GridLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

Tailwind class: `grid` + `className` passthrough.

CSS inline styles (from props):

| Prop | Style property | Conversion |
|---|---|---|
| `columns` (number) | `gridTemplateColumns` | `repeat(N, 1fr)` |
| `columns` (string) | `gridTemplateColumns` | passthrough |
| `rows` (number) | `gridTemplateRows` | `repeat(N, 1fr)` |
| `rows` (string) | `gridTemplateRows` | passthrough |
| `gap` (number) | `gap` | `Npx` |
| `gap` (string) | `gap` | passthrough |
| `align` | `alignItems` | passthrough |
| `justify` | `justifyItems` | passthrough |

---

## 2. Item container

Only `className` applied (via `cn(className)`).

CSS inline styles:

| Prop combination | `gridRow` value |
|---|---|
| `rowSpan` set | `${row ?? 'auto'} / span ${rowSpan}` |
| `row` only | `String(row)` |
| Neither | `undefined` |

Same pattern for `colSpan`/`col` → `gridColumn`.
