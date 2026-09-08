# TileLayout — Styling Contract

- **Component:** TileLayout
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TileLayout.Semantic.md) · [Interaction](./TileLayout.Interaction.md) · [Accessibility](./TileLayout.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/TileLayout.tsx`
- **Catalog row:** #135 TileLayout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Grid container

`grid` + inline styles:
- `gridTemplateColumns: repeat(${columns}, 1fr)`
- `gap: ${gap}px` (or gap string)

---

## 2. Tile item

Base: `flex flex-col rounded-lg border border-border bg-card overflow-hidden transition-opacity`
Draggable: `cursor-grab active:cursor-grabbing`
Dragging (source): `opacity-40`
Drag-over (target): `ring-2 ring-primary`

Inline style:
- `gridColumn: '${col} / span ${colSpan}'` (when col provided) or `'span ${colSpan}'`
- `gridRow: '${row} / span ${rowSpan}'` (when row provided) or `'span ${rowSpan}'`
- `minHeight: calc(${rowH} * ${rowSpan} + ${rowSpan-1}px)`

---

## 3. Tile header (when provided)

`flex items-center justify-between border-b border-border px-4 py-2 bg-muted/30`

Header text: `text-sm font-medium`

---

## 4. Tile body

`flex-1 overflow-auto p-4`
