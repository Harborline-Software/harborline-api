# Typography — Semantic Contract

- **Component:** Typography
- **ADR 0017 family:** Typography
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** #143 Typography (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native HTML semantic elements with Tailwind typography classes

---

## 1. Component purpose

Type scale component. Maps a `variant` prop to a consistent typographic style. Renders the semantically appropriate HTML element by default, with an `as` escape hatch for custom elements.

---

## 2. Props

```typescript
type TypographyVariant =
  | 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'
  | 'body1' | 'body2' | 'caption' | 'overline' | 'label'

interface TypographyProps {
  variant: TypographyVariant
  as?: ElementType     // override the default HTML element
  className?: string
  children?: ReactNode
}
```

---

## 3. Default element map

| Variant | Default element |
|---|---|
| `h1` | `<h1>` |
| `h2` | `<h2>` |
| `h3` | `<h3>` |
| `h4` | `<h4>` |
| `h5` | `<h5>` |
| `h6` | `<h6>` |
| `body1` | `<p>` |
| `body2` | `<p>` |
| `caption` | `<span>` |
| `overline` | `<span>` |
| `label` | `<span>` |

The `as` prop overrides the default element without changing the applied styles.

---

## 4. Convenience aliases

The following convenience components are exported:

`H1`, `H2`, `H3`, `H4`, `H5`, `H6`, `Body1`, `Body2`, `Caption`, `Overline`

Each is `Typography` with the variant pre-set. They accept all `TypographyProps` except `variant`.

No `Label` alias is exported to avoid name collision with the `Label` component.

---

## 5. Visual hierarchy

| Variant | Scale description |
|---|---|
| `h1` | Page title — largest, `4xl/5xl` |
| `h2` | Section heading — `3xl` |
| `h3` | Sub-section heading — `2xl` |
| `h4` | Card/panel heading — `xl` |
| `h5` | Minor heading — `lg` |
| `h6` | Small heading — `base` |
| `body1` | Default body text — `base` |
| `body2` | Secondary body text — `sm` |
| `caption` | Helper/caption text — `xs muted` |
| `overline` | Label prefix — `xs uppercase tracking-widest muted` |
| `label` | Form label equivalent — `sm font-medium` |
