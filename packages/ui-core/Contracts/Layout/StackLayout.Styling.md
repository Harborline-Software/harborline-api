# StackLayout — Styling Contract

- **Component:** StackLayout
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StackLayout.Semantic.md) · [Interaction](./StackLayout.Interaction.md) · [Accessibility](./StackLayout.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/StackLayout.tsx`
- **Catalog row:** #127 StackLayout (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Tailwind: `flex` + orientation class + `className` passthrough.

| orientation | Class |
|---|---|
| `vertical` | `flex-col` |
| `horizontal` | `flex-row` |

CSS inline styles:

| Prop | Style property | Conversion |
|---|---|---|
| `gap` (number) | `gap` | `Npx` |
| `gap` (string) | `gap` | passthrough |
| `align` | `alignItems` | passthrough |
| `justify` | `justifyContent` | passthrough |
| `wrap=true` | `flexWrap` | `'wrap'` |
| `wrap=false` | `flexWrap` | `undefined` (default browser behavior) |
