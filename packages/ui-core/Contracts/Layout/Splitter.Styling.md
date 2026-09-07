# Splitter — Styling Contract

- **Component:** Splitter
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Splitter.Semantic.md) · [Interaction](./Splitter.Interaction.md) · [Accessibility](./Splitter.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Splitter.tsx`
- **Catalog row:** #125 Splitter (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex overflow-hidden`
Vertical orientation adds: `flex-col`

---

## 2. Pane

Inline style: `width: {sizes[i]}px` (horizontal) or `height: {sizes[i]}px` (vertical), `overflow: auto`
No Tailwind classes on pane `<div>`.

---

## 3. Divider

Base: `shrink-0 bg-border hover:bg-primary/40 transition-colors`

| Orientation | Size class | Cursor |
|---|---|---|
| `horizontal` | `w-1` | `cursor-col-resize` |
| `vertical` | `h-1` | `cursor-row-resize` |
