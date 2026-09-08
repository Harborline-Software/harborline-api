# CRUDHelper / DataForm — Semantic Contract

- **Component:** CRUDHelper / DataForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CRUDHelper.Interaction.md) · [Accessibility](./CRUDHelper.Accessibility.md) · [Styling](./CRUDHelper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Vaadin CRUD)
- **Catalog row:** #A9 CRUDHelper / DataForm (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin CRUD baseline)

---

## 1. Component purpose

**CRUDHelper** (alias: DataForm) — a composition component that combines a DataGrid or ListView for record listing with a Form panel for create/edit operations. Provides a coordinated grid+form layout for standard CRUD workflows without requiring callers to wire up the list-to-form integration manually.

---

## 2. Props (planned)

```typescript
interface CRUDHelperProps<T = Record<string, unknown>> {
  data: T[]
  columns: DataGridColumn<T>[]
  formRender: (item: T | null, mode: 'create' | 'edit') => React.ReactNode
  onSave: (item: T, mode: 'create' | 'edit') => void | Promise<void>
  onDelete?: (item: T) => void | Promise<void>
  onCancel?: () => void
  idField?: keyof T & string          // default: 'id'
  title?: string
  addLabel?: string                    // default: 'New item'
  editLabel?: string                   // default: 'Edit item'
  layout?: 'split' | 'modal'          // default: 'split'
  className?: string
}
```

---

## 3. Layout modes

**split**: grid on the left, form panel on the right. Selecting a grid row loads it into the form. "New" button clears the form for creation.

**modal**: grid full-width. Edit/create opens a Dialog with the form. On save/cancel, the dialog closes.

---

## 4. State machine

States: `list` (no selection) → `edit` (row selected → form loaded) → `create` (New clicked → blank form). `onSave` transitions back to `list`. `onCancel` transitions back to `list`.

---

## 5. Relationship to Form + DataGrid

CRUDHelper composes `Form`, `DataGrid`, and optional `Dialog`. It does not introduce new form primitives — `formRender` is caller-provided and uses the existing form component set.
