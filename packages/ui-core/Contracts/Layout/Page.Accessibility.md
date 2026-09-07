# Page — Accessibility Contract

- **Component:** Page
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Page.Semantic.md) · [Interaction](./Page.Interaction.md) · [Styling](./Page.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Page.tsx`
- **Catalog row:** Page (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Landmark structure

Page composes a `<header>` landmark (via PageHeader) and a `<section>` body region. The `title`
renders as the route heading (`h1` by default, override via `titleAs`) — exactly one `h1` per route
shell keeps the heading outline correct.

---

## 2. Scrollable body

The body `<section>` is keyboard-scrollable by default because it is a real scroll container; users
on keyboard can focus interactive children and the viewport follows. A scroll region containing no
focusable content is acceptable here because route bodies normally contain focusable content; when a
route body is purely static and overflows, callers should add `tabIndex={0}` + an accessible name to
the section (passed through `bodyClassName` is not sufficient — use a wrapping landmark in that rare
case).

---

## 3. Header accessibility

Delegated to PageHeader (see PageHeader.Accessibility) — landmark `<header>`, real heading, AA
contrast over scrolling content.

---

## 4. Known gaps

None identified for the common case (route body with focusable content).
