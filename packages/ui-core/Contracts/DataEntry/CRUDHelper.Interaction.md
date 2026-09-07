# CRUDHelper / DataForm — Interaction Contract

- **Component:** CRUDHelper / DataForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CRUDHelper.Semantic.md) · [Accessibility](./CRUDHelper.Accessibility.md) · [Styling](./CRUDHelper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A9 CRUDHelper / DataForm (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin CRUD baseline)

---

## 1. Create

"New item" button: clears form, sets mode to `create`, shows form. Save calls `onSave(newItem, 'create')`, transitions back to `list`.

---

## 2. Edit

Selecting a grid row loads the item into `formRender(item, 'edit')`. Save calls `onSave(item, 'edit')`, transitions to `list`.

---

## 3. Delete

Delete button (per row or in form): calls `onDelete(item)`. Caller handles confirmation if needed. Transitions to `list`.

---

## 4. Cancel

Cancel calls `onCancel?.()`, transitions to `list`. Form is discarded.

---

## 5. Save loading

While `onSave` is pending, form buttons are disabled (loading state). Caller manages error display.

---

## 6. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
