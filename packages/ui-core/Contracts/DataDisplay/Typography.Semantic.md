# Typography — Semantic Contract

- **Component:** Typography
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native HTML semantic elements with Tailwind typography classes

---

## 1. Component purpose

**Typography** — a variant-driven text rendering component that maps named semantic variants to consistent heading and body text styles. The canonical way to render headings, body copy, captions, overlines, and labels in `@harborline-software/ui-react`. Ensures typographic consistency without coupling callers to specific Tailwind class strings.

---

## 2. Props

```typescript
type TypographyVariant =
  | 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'
  | 'body1' | 'body2' | 'caption' | 'overline' | 'label'

interface TypographyProps {
  variant: TypographyVariant     // required
  as?: React.ElementType         // override the rendered element
  className?: string             // merged with variant classes via cn()
  children?: React.ReactNode
}
```

---

## 3. Variants and default elements

| Variant | Default element | Description |
|---|---|---|
| `h1` | `<h1>` | Page title — largest heading |
| `h2` | `<h2>` | Section heading |
| `h3` | `<h3>` | Sub-section heading |
| `h4` | `<h4>` | Card or panel heading |
| `h5` | `<h5>` | Minor heading |
| `h6` | `<h6>` | Smallest heading |
| `body1` | `<p>` | Primary body copy |
| `body2` | `<p>` | Secondary / smaller body copy |
| `caption` | `<span>` | Small auxiliary text (image captions, footnotes) |
| `overline` | `<span>` | All-caps label above a title or section |
| `label` | `<span>` | Form field label or tight metadata label |

---

## 4. `as` prop (polymorphic)

The `as` prop overrides the default element. Common overrides:

- `<Typography variant="h2" as="h3">` — visual h2 style on an h3 element for semantic heading hierarchy.
- `<Typography variant="body1" as="div">` — body1 style on a div container.
- `<Typography variant="label" as="label">` — renders an HTML `<label>` (for form fields).

When `as` is not provided, the default element for the variant is used (see table above).

---

## 5. Convenience aliases

The module exports named convenience components for all variants:

```typescript
H1, H2, H3, H4, H5, H6
Body1, Body2
Caption, Overline
```

Each alias is `Omit<TypographyProps, 'variant'>` — the `variant` is pre-set. Example: `<H2>Section title</H2>`.

Note: `Label` is not exported as a convenience alias (would shadow the HTML `<label>` element name in some import contexts). Use `<Typography variant="label">` or pass `as="label"` explicitly.

---

## 6. className merge

`className` is merged with the variant class string via `cn()`. Host classes applied via `className` augment or override the variant defaults.

---

## 7. Related components

- **Badge** — compact status pill, not a typography token.
- **Overline** pattern — `<Typography variant="overline">` is the canonical overline; avoid ad-hoc `text-xs uppercase tracking-widest` inline classes.
