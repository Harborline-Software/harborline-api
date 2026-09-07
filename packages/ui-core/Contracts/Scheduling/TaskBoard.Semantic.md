# TaskBoard — Semantic Contract

- **Component:** TaskBoard
- **ADR 0017 family:** Scheduling
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./TaskBoard.Interaction.md) · [Accessibility](./TaskBoard.Accessibility.md) · [Styling](./TaskBoard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #117 TaskBoard (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik TaskBoard / Trello-pattern baseline)

---

## 1. Component purpose

**TaskBoard** — a Kanban-style board with draggable task cards organized into swimlane columns. Supports custom column definitions, card templates, add/edit/delete actions, and multi-column drag-and-drop reordering.

---

## 2. Data model

```typescript
interface TaskBoardCard {
  id: string | number
  title: string
  description?: string
  columnId: string | number
  order?: number
  color?: string
  tags?: string[]
  assignee?: { name: string; avatarUrl?: string }
  dueDate?: Date
  [key: string]: unknown
}

interface TaskBoardColumn {
  id: string | number
  title: string
  color?: string
  maxCards?: number
  addButton?: boolean
}

interface TaskBoardProps {
  columns: TaskBoardColumn[]
  cards: TaskBoardCard[]
  onCardMove?: (cardId: string | number, targetColumnId: string | number, targetOrder: number) => void
  onCardAdd?: (columnId: string | number) => void
  onCardEdit?: (card: TaskBoardCard) => void
  onCardDelete?: (cardId: string | number) => void
  cardRender?: (card: TaskBoardCard) => React.ReactNode
  columnHeaderRender?: (column: TaskBoardColumn) => React.ReactNode
  editable?: boolean
  className?: string
}
```

---

## 3. Column behavior

Cards in each column are sorted by `order` ascending. If `order` is omitted, insertion order is used. `maxCards` on a column disables drag-drop acceptance when the column is at capacity and hides the add button.

---

## 4. Card rendering

Default card renders: title (bold), description (truncated to 2 lines), tags as badges, assignee avatar, due date. `cardRender` overrides the entire card surface.

---

## 5. Editing

When `editable=true`: add-card button appears per column (if `column.addButton !== false`), cards have an edit icon (pencil) and delete icon on hover. Edit/delete fire `onCardEdit` / `onCardDelete` — the parent is responsible for opening an edit dialog.
