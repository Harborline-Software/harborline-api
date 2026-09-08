# PageHeader — Accessibility Contract

- **Component:** PageHeader
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PageHeader.Semantic.md) · [Interaction](./PageHeader.Interaction.md) · [Styling](./PageHeader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/PageHeader.tsx`
- **Catalog row:** PageHeader (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Landmark + heading semantics

Renders a native `<header>` landmark. The `title` renders as a real heading element (`h1` by
default, override via `titleAs`) so it participates in the document heading outline and is reachable
by AT heading navigation. Callers placing multiple PageHeaders in one document should set `titleAs`
to keep the outline coherent (a route shell has exactly one `h1`).

---

## 2. Subtitle

The subtitle is a plain `<p>` associated visually with the title; it is supplementary text, not a
heading, and is read in normal reading order after the title.

---

## 3. Actions

Accessibility of the `actions` slot belongs to the caller's children (buttons need accessible names,
pills need sufficient contrast, etc.). PageHeader adds no ARIA over them.

---

## 4. Contrast

`bg-background/80` + `backdrop-blur` preserve text contrast over scrolling content; title and
subtitle use foreground / muted-foreground tokens that meet WCAG AA against the surface.

---

## 5. Known gaps

None identified.
