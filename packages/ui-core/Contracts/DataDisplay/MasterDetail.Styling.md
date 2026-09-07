# MasterDetail — Styling Contract

- **Component:** MasterDetail
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MasterDetail.Semantic.md) · [Interaction](./MasterDetail.Interaction.md) · [Accessibility](./MasterDetail.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 MasterDetail (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin Details baseline)

---

## 1. Root container

**side-by-side**: `flex h-full gap-0 border border-border rounded-md overflow-hidden`

**stacked**: `flex flex-col border border-border rounded-md overflow-hidden`

---

## 2. Master pane

Side-by-side: `border-r border-border overflow-y-auto` + width from `masterWidth` prop (default `40%`)

Stacked: `border-b border-border overflow-y-auto`

---

## 3. Master items

Base: `flex items-center px-4 py-3 cursor-pointer text-sm border-b border-border hover:bg-accent transition-colors`

Selected: `bg-accent text-accent-foreground font-medium`

---

## 4. Detail pane

`flex-1 overflow-y-auto p-4 bg-background`

---

## 5. Design tokens

Uses design tokens: `border-border`, `bg-accent`, `text-accent-foreground`, `bg-background`.
