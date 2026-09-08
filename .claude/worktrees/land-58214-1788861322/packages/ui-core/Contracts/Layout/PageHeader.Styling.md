# PageHeader — Styling Contract

- **Component:** PageHeader
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PageHeader.Semantic.md) · [Interaction](./PageHeader.Interaction.md) · [Accessibility](./PageHeader.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/PageHeader.tsx`
- **Catalog row:** PageHeader (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Container

`<header>` — `flex h-14 items-center justify-between gap-4 border-b border-border
bg-background/80 px-6 backdrop-blur`. Fixed comfortable bar height (`h-14`); design-system tokens
only (`border`, `background`). The translucent `bg-background/80` + `backdrop-blur` keep the header
legible over scrolling body content when `sticky`.

---

## 2. Sticky mode

When `sticky` is set, adds `sticky top-0 z-10` so the header pins to the top of its scroll
container and layers above scrolling content.

---

## 3. Title cluster

Left cluster `min-w-0 leading-tight`. Title: `truncate text-base font-semibold`. Subtitle:
`truncate text-[11px] text-muted-foreground`. `min-w-0` + `truncate` prevent long titles from
pushing the `actions` cluster off-screen.

---

## 4. Actions cluster

Right cluster `flex shrink-0 items-center gap-2`. `shrink-0` keeps actions fully visible; the title
cluster yields width first.

---

## 5. className passthrough

`className` is merged onto the root `<header>` via `cn()`. All other unlisted props (`...rest`) are
also forwarded to the root `<header>`.
