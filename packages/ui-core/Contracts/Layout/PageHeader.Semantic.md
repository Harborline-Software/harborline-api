# PageHeader — Semantic Contract

- **Component:** PageHeader
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PageHeader.Interaction.md) · [Accessibility](./PageHeader.Accessibility.md) · [Styling](./PageHeader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/PageHeader.tsx`
- **Catalog row:** PageHeader (`app-priority: high`, `library-scope: in-scope`) — generic per-route chrome
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18 catalog-gap addition; Harborline App `RoutePage` header is the reference shape)
- **Interaction-class:** display-only (no intrinsic interactivity; the `actions` slot may host interactive children)

---

## 1. Component purpose

**PageHeader** — a per-route title bar: a `title` (rendered as a heading) with an optional
`subtitle`, plus a trailing `actions` slot for status pills / action buttons. Generic layout
chrome only — it carries **no domain concepts**. Gives every route a visually consistent header
without each route re-deriving the markup. Pairs with {@link Page} for the standard
"header over scrollable content" route shell.

---

## 2. Props

```typescript
interface PageHeaderProps extends React.HTMLAttributes<HTMLElement> {
  title: React.ReactNode            // required; rendered as the heading
  subtitle?: React.ReactNode        // optional supporting line under the title
  actions?: React.ReactNode         // trailing slot, right-aligned (pills, buttons)
  titleAs?: 'h1' | 'h2' | 'h3'      // heading level; default 'h1'
  sticky?: boolean                  // stick to top of the scroll container; default false
  children?: React.ReactNode
  // ...rest forwarded to the root <header> (id, data-*, aria-*, handlers)
}
```

---

## 3. Implementation strategy

A single `<header>` flex row: a left cluster (title heading + optional subtitle paragraph) and a
right cluster (the `actions` slot). The title uses the `titleAs` tag (default `h1`). Both clusters
truncate gracefully under width pressure. `...rest` is forwarded to the root `<header>` so callers
can attach `id`, `data-testid`, `aria-*`, and handlers.

---

## 4. Children

`title` / `subtitle` / `actions` are the structured slots. Additional `children` render after the
`actions` cluster inside the header (rare; the structured slots cover the common case).
