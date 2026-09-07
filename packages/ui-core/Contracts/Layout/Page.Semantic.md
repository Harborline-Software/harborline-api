# Page — Semantic Contract

- **Component:** Page
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Page.Interaction.md) · [Accessibility](./Page.Accessibility.md) · [Styling](./Page.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Page.tsx`
- **Catalog row:** Page (`app-priority: high`, `library-scope: in-scope`) — generic route shell
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18 catalog-gap addition; Harborline App `RoutePage` is the reference shape)
- **Interaction-class:** display-only (layout shell; interactivity delegated to header `actions` + body children)

---

## 1. Component purpose

**Page** — the standard route shell: composes a {@link PageHeader} over a scrollable body region.
Generic layout chrome only — **no domain concepts**. Encodes the common "sticky header over
scrollable content" pattern once so routes don't each re-derive the flex-column + overflow plumbing.

---

## 2. Props

```typescript
interface PageProps extends React.HTMLAttributes<HTMLDivElement> {
  title: React.ReactNode                          // forwarded to PageHeader
  subtitle?: React.ReactNode                       // forwarded to PageHeader
  actions?: React.ReactNode                         // forwarded to PageHeader trailing slot
  titleAs?: 'h1' | 'h2' | 'h3'                       // heading level; default 'h1'
  sticky?: boolean                                   // header sticks to top; default true
  bodyPadding?: 'none' | 'sm' | 'md' | 'lg'          // scrollable body padding; default 'md'
  bodyClassName?: string                              // extra classes for the body <section>
  children?: React.ReactNode                          // the route body
  // ...rest forwarded to the root <div>
}
```

---

## 3. Implementation strategy

A flex-column root (`flex h-full min-h-0 flex-col`) holds two children: the composed `PageHeader`
(passed `title` / `subtitle` / `actions` / `titleAs` / `sticky`) and a `<section>` body that grows
to fill remaining height (`flex-1 overflow-auto`) and scrolls independently. `bodyPadding` selects
the section padding; `min-h-0` lets the body actually shrink-to-scroll inside a flex parent.

---

## 4. Children

`children` is the route body, rendered inside the scrollable `<section>`. The header is built from
the structured props, not from children.
