# TaskBoard — Interaction Contract

- **Component:** TaskBoard
- **ADR 0017 family:** Scheduling
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./TaskBoard.Semantic.md) · [Accessibility](./TaskBoard.Accessibility.md) · [Styling](./TaskBoard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #117 TaskBoard (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik TaskBoard / Trello-pattern baseline)

---

## 1. Drag-and-drop

When `editable=true`, cards are draggable via pointer. Dragging a card shows a ghost at 60% opacity. Drop zones highlight on hover. On drop, `onCardMove` fires with `(cardId, targetColumnId, targetOrder)` where `targetOrder` is the 0-based index within the column. No reordering within the same column unless drop position differs.

---

## 2. Add card

Click the "+" button at the bottom of a column → `onCardAdd(columnId)` fires. The parent is responsible for opening an add dialog; TaskBoard does not manage inline editing.

---

## 3. Edit / delete

Hover over a card → pencil icon and trash icon appear. Pencil click → `onCardEdit(card)`. Trash click → `onCardDelete(cardId)`. Both fire only when `editable=true`.

---

## 4. Column scroll

Each column scrolls independently (vertical overflow) when cards exceed the visible area. The board itself scrolls horizontally when columns exceed the viewport.

---

## 5. Column capacity

When a column reaches `maxCards`, the drop target for that column is disabled and shows a `cursor-not-allowed` indicator. The add button is hidden.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TSKBD1 | High | Drag-and-drop is pointer-only; no keyboard card-move equivalent | Accepted-risk M1; keyboard path via edit dialog (change column field) |
| G-TSKBD2 | Medium | Column reordering (drag column headers) is not in scope | Deferred post-M1; static column order only |

---

## Full-surface expansion (2026-06-11 — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — dynamic column add / edit / delete

**Status:** Draft

#### §FS-1.1 Column management props

```typescript
// Additions to TaskBoardProps:
onColumnAdd?: () => void
onColumnEdit?: (column: TaskBoardColumn) => void
onColumnDelete?: (columnId: string | number) => void
columnEditable?: boolean   // default: false; shows column management affordances
```

`columnEditable` is intentionally separate from `editable` (card editing) to allow
boards where cards are editable but the column structure is locked, or vice versa.

#### §FS-1.2 Column add

When `columnEditable === true`, a "+" column stub renders after the last column.
Clicking it fires `onColumnAdd()`. The parent is responsible for appending a new
`TaskBoardColumn` to `columns` — TaskBoard does not manage inline column creation.

The "+" stub renders at the same height as the tallest current column (or a minimum
height of 200px if the board is empty).

#### §FS-1.3 Column edit

When `columnEditable === true`, each column header gains an edit affordance
(pencil icon button, visible on hover / keyboard focus).
Clicking it fires `onColumnEdit(column)`. The parent is responsible for opening an
edit dialog. Changes are reflected by the host updating `columns` via the controlled
prop.

Double-clicking the column title itself enters inline rename mode:
- The title text becomes an editable `<input>` in-place.
- Pressing Enter or blurring commits; fires `onColumnEdit({ ...column, title: newTitle })`.
- Pressing Escape reverts without firing.

#### §FS-1.4 Column delete

When `columnEditable === true`, each column header gains a "Delete column" option in a
column action menu (ellipsis icon — adopts FR-2 popup pattern).
Selecting it fires `onColumnDelete(columnId)`.

The last remaining column cannot be deleted — the "Delete column" option is disabled
when only one column exists.

When a column is deleted, cards in that column are orphaned: they remain in `cards`
with their `columnId` pointing to the deleted column. The host is responsible for
reassigning or removing them.

**wave-2**

---

### §FS-2 Wave-2 — card preview pane

**Status:** Draft

#### §FS-2.1 Preview pane model

```typescript
// Additions to TaskBoardProps:
previewPaneRender?: (card: TaskBoardCard, onClose: () => void) => React.ReactNode
```

When `previewPaneRender` is supplied, clicking on a card (anywhere except the edit/
delete icon buttons) opens the preview pane.

The preview pane renders as a slide-in panel anchored to the right side of the
`TaskBoard` container (`position: absolute; right: 0; top: 0; height: 100%`).
The board columns shift left to make room (the board container shrinks its width by
the panel width; the column layout reflows).

`previewPaneRender(card, onClose)` returns the panel content. `onClose` dismisses the
panel. The panel is also dismissed by pressing Escape or clicking outside the panel
(on the board area).

Only one preview pane is open at a time. Clicking a different card while the pane is
open replaces the content with the new card's preview (no intermediate close/open
animation).

FR-2 applies: `previewOpen?: boolean` / `onPreviewOpenChange?: (open: boolean) => void`
may be used for controlled pane state.

**wave-2**

---

### §FS-3 Wave-3 — inline edit form with validation

**Status:** Draft

#### §FS-3.1 Edit form model

```typescript
interface TaskBoardEditField {
  name: string               // matches a key in TaskBoardCard
  label: string
  type: 'text' | 'textarea' | 'date' | 'select'
  options?: Array<{ label: string; value: string | number }>  // for 'select'
  required?: boolean         // per FR-1
  validate?: (value: unknown) => string | null   // returns error message or null
}

// Additions to TaskBoardProps:
editFields?: TaskBoardEditField[]      // enables built-in inline edit form
onCardSave?: (card: TaskBoardCard) => void   // receives updated card on form submit
```

When `editFields` is supplied, the pencil-icon click (currently fires `onCardEdit`)
instead opens a built-in edit form rendered within the preview pane (if available) or
as an inline expansion below the card.

#### §FS-3.2 Edit form behavior

The edit form renders a field for each entry in `editFields`, pre-populated with the
current card's values.

- Required fields (FR-1 `required === true`) carry `aria-required="true"`. Submitting
  the form with an empty required field shows an error message below the field
  (FormField / ValidationMessage composition pattern per FR-1).
- `validate()` is called on blur and on submit. A non-null return is shown as the
  field's validation message.
- Clicking "Save" (or pressing Enter on a single-line input not adjacent to another
  field): runs full validation. If all fields pass, fires `onCardSave(updatedCard)`
  where `updatedCard` merges the edited field values onto the original card object.
  The edit form closes.
- Clicking "Cancel" (or pressing Escape): closes the form without firing any callbacks;
  all inputs revert to their pre-edit values.

#### §FS-3.3 Edit form vs onCardEdit coexistence

When `editFields` is supplied:
- Built-in form takes over the pencil-icon click. `onCardEdit` is NOT called.
- `onCardSave` receives the committed card.

When `editFields` is absent:
- Legacy behavior: pencil-icon fires `onCardEdit(card)`.

This is a backward-compatible extension — hosts already using `onCardEdit` are
unaffected until they add `editFields`.

**wave-3**

---

### §FS-4 Wave-3 — card search and filter

**Status:** Draft

#### §FS-4.1 Search prop

```typescript
// Addition to TaskBoardProps:
search?: string    // host-controlled; filters visible cards by title/description contains-match
```

When `search` is non-empty, cards are filtered client-side: a card is visible when
`card.title` or `card.description` contains `search` (case-insensitive).

Columns with no visible cards after filtering render with an empty state
placeholder (configurable via `emptyColumnRender`). Column capacity (§5 of existing
contract) is evaluated against the full unfiltered card count, not the filtered count.

Drag-and-drop is disabled while `search` is non-empty (an active search filter means
the visual layout does not represent the full column state; moving a card in this mode
would be confusing).

#### §FS-4.2 Priority field + visual indicator

```typescript
type TaskBoardPriority = 'low' | 'medium' | 'high' | 'critical'

// Addition to TaskBoardCard:
priority?: TaskBoardPriority
```

When `priority` is set, the card renders a priority indicator (colored left-border bar
or a labeled badge; design token surface owned by PAO Styling §FS). The four priority
values map to design tokens: `priority-low`, `priority-medium`, `priority-high`,
`priority-critical`.

The `color` bar (existing `TaskBoardCard.color` prop) is distinct from the priority
indicator: `color` is a freeform accent bar; `priority` is a semantic indicator.
Both can be present simultaneously.

**wave-3**
