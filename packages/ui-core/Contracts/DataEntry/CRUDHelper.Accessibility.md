# CRUDHelper / DataForm — Accessibility Contract

- **Component:** CRUDHelper / DataForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CRUDHelper.Semantic.md) · [Interaction](./CRUDHelper.Interaction.md) · [Styling](./CRUDHelper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 CRUDHelper / DataForm (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin CRUD baseline)

---

## 1. Grid accessibility

The embedded grid follows the DataGrid accessibility contract — `role="grid"`, row selection, keyboard navigation.

---

## 2. Form accessibility

The embedded form follows the Form accessibility contract — all inputs have labels, required indicators, and `aria-describedby` for error messages.

---

## 3. Mode transitions

When transitioning to edit or create mode, focus moves to the first form field. When returning to list mode, focus returns to the "New item" button or the previously selected row.

---

## 4. Modal layout

In modal mode, the Dialog follows the Dialog accessibility contract — focus trap, `role="dialog"`, `aria-labelledby`.

---

## 5. Known gaps

None identified for forward-spec.
