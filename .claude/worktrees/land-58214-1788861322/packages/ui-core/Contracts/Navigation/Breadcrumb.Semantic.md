# Breadcrumb — Semantic Contract

- **Component:** Breadcrumb
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Breadcrumb.Interaction.md) · [Accessibility](./Breadcrumb.Accessibility.md) · [Styling](./Breadcrumb.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Breadcrumb.tsx`
- **Catalog row:** #14 Breadcrumb (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<nav>` + `<ol>` (hand-rolled)

---

## 1. Component purpose

**Breadcrumb** — a navigation trail showing the user's current location within a hierarchy. Items render as links (`<a>`) or static text depending on whether `href` is provided and whether the item is current.

---

## 2. Data model

```typescript
interface BreadcrumbItem {
  label: string
  href?: string
  current?: boolean
}
```

---

## 3. Props

```typescript
interface BreadcrumbProps extends Omit<React.HTMLAttributes<HTMLElement>, 'aria-label'> {
  items: BreadcrumbItem[]
  separator?: React.ReactNode   // default: chevron SVG
  ariaLabel?: string            // default: "Breadcrumb" — labels the <nav> landmark
}
```

---

## 4. Current item detection

`isCurrent = item.current ?? isLast`

The last item is automatically treated as current unless `current` is explicitly set. The current item renders as a `<span>` even when `href` is provided (no clickable link for the current page).

---

## 5. Rendering rules

| Condition | Element |
|---|---|
| `href` provided AND not current | `<a href>` (link) |
| Current (last or `item.current=true`) | `<span aria-current="page">` |
| No href AND not current | `<span>` (non-clickable intermediate) |

Separators are wrapped in `<span aria-hidden="true">`.
