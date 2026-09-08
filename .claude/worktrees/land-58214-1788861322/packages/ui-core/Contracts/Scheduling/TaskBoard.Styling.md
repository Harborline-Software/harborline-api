# TaskBoard — Styling Contract

- **Component:** TaskBoard
- **ADR 0017 family:** Scheduling
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./TaskBoard.Semantic.md) · [Interaction](./TaskBoard.Interaction.md) · [Accessibility](./TaskBoard.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #117 TaskBoard (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik TaskBoard / Trello-pattern baseline)

---

## 1. Board layout

Root: `flex gap-4 overflow-x-auto p-4 min-h-[400px]`. Each column: `flex flex-col w-[280px] flex-shrink-0`.

---

## 2. Column header

`rounded-t-md px-3 py-2 font-semibold text-sm`. Background: `bg-muted`. Custom column color: `style={{ borderTop: '3px solid {column.color}' }}`. Card count badge: `ml-auto text-xs text-muted-foreground`.

---

## 3. Column body

`flex-1 bg-muted/40 rounded-b-md p-2 flex flex-col gap-2 min-h-[100px]`. Drop zone active: `outline-2 outline-dashed outline-primary/40 bg-primary/5`.

---

## 4. Cards

`bg-card border border-border rounded-md p-3 shadow-sm cursor-grab hover:shadow-md transition-shadow`. Dragging ghost: `opacity-60 rotate-1 shadow-lg cursor-grabbing`. Color accent: `border-l-4 style={{ borderLeftColor: card.color }}` (only when `card.color` is set).

---

## 5. Card content

Title: `text-sm font-medium text-foreground`. Description: `text-xs text-muted-foreground line-clamp-2 mt-1`. Tags: `flex flex-wrap gap-1 mt-2`; each tag: `text-[10px] bg-muted rounded px-1.5 py-0.5`. Due date: `text-xs text-muted-foreground mt-1`. Assignee avatar: `w-5 h-5 rounded-full` in the bottom-right corner.

---

## 6. Edit/delete icons

Revealed on card hover: `absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity`. Each button: `h-6 w-6 p-1 rounded text-muted-foreground hover:text-foreground hover:bg-muted`.

---

## 7. Add button

`w-full text-left text-sm text-muted-foreground hover:text-foreground px-2 py-1.5 rounded hover:bg-muted/60 transition-colors mt-1 flex items-center gap-1`.

---

## 8. Design tokens

Uses: `hsl(var(--card))`, `hsl(var(--muted))`, `hsl(var(--border))`, `hsl(var(--primary))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--background))`.
