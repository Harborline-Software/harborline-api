# TaskBoard — Accessibility Contract

- **Component:** TaskBoard
- **ADR 0017 family:** Scheduling
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./TaskBoard.Semantic.md) · [Interaction](./TaskBoard.Interaction.md) · [Styling](./TaskBoard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #117 TaskBoard (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik TaskBoard / Trello-pattern baseline)

---

## 1. Board structure

Board root: `role="region" aria-label="Task board"`. Each column: `role="group" aria-label="{column.title} — {n} cards"`. Card list within column: `role="list"`.

---

## 2. Cards

Each card `<li>` carries implicit `listitem` role from the `role="list"` parent. When `editable=true`, the card must NOT also carry `role="button"` on the same element — two conflicting roles on one element is invalid per WAI-ARIA 1.2. Correct pattern: the card `<li>` (listitem) contains an inner `<button>` child that carries `aria-label="{card.title}{dueDate ? ', due {date}' : ''}"` and `tabIndex={0}`; the outer `<li>` remains a pure listitem. See G-TSKBD-A3.

---

## 3. Action buttons

Edit button: `aria-label="Edit {card.title}"`. Delete button: `aria-label="Delete {card.title}"`. Add button: `aria-label="Add card to {column.title}"`.

---

## 4. Drag-and-drop

Pointer-based drag has no keyboard equivalent (see G-TSKBD-A1). Screen reader users must use the edit dialog to change a card's column.

---

## 5. Live region

When a card is moved, an `aria-live="polite"` region announces: `"{card.title} moved to {targetColumn.title}"`.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TSKBD-A1 | High | Drag-and-drop is pointer-only — no keyboard card-move | Accepted-risk M1; AT users change column via edit dialog |
| G-TSKBD-A2 | Medium | Column capacity limit not announced to AT | Accepted-risk M1; add button hidden when at capacity; AT reads absence of add button |
| G-TSKBD-A3 | High | Prior spec placed `role="button"` on the same `<li>` as the implicit listitem role — invalid WAI-ARIA 1.2 (two conflicting roles on one element); fix per §2: inner `<button>` child pattern | Fix-in-M1: implement `<li>` (listitem) + inner `<button>` (keyboard activation) two-element pattern |
