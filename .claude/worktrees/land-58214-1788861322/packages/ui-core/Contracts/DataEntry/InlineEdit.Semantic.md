# InlineEdit — Semantic Contract

- **Component:** InlineEdit
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./InlineEdit.Interaction.md) · [Accessibility](./InlineEdit.Accessibility.md) · [Styling](./InlineEdit.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/InlineEdit.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input>` toggled by click/double-click

---

## 1. Purpose

InlineEdit renders a value as a button in view mode and replaces it with an
`<input>` in edit mode. The transition is triggered by clicking or activating
the button. Editing is committed on blur or Enter, cancelled on Escape. It is
suited for list headers, table cells, or anywhere an in-place rename/value edit
is needed without opening a dialog.

---

## 2. Data model

```typescript
interface InlineEditProps {
  value: string
  onConfirm: (value: string) => void
  placeholder?: string
  maxLength?: number
  className?: string
  renderDisplay?: (value: string) => React.ReactNode
  'aria-label'?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | _required_ | Current committed value. InlineEdit does not own this — it is the host's value. |
| `onConfirm` | `(value: string) => void` | _required_ | Called when the edit is committed (blur or Enter). Only fires when the trimmed draft is non-empty AND differs from the current value. |
| `placeholder` | `string` | `'Click to edit'` | Text shown when `value` is empty in view mode. |
| `maxLength` | `number` | — | Passed to the edit `<input maxLength>`. |
| `className` | `string` | — | Applied to both the view button and the edit input. |
| `renderDisplay` | `(value: string) => ReactNode` | — | When provided, used to render the value in view mode (e.g. to add formatting or icons). |
| `'aria-label'` | `string` | — | When provided: the edit input gets this label; the view button gets `"Edit {aria-label}"`. When omitted: the view button gets `"Click to edit"`. |

### 3.1 Edit mode entry

Clicking (or keyboard-activating) the view button transitions to edit mode:

1. `setEditing(true)` triggers a `useEffect` that: copies `value` into `draft`,
   and calls `inputRef.current?.select()` to select all text in the input.
2. The input receives `autoFocus`.

### 3.2 Commit semantics

`onConfirm` fires only when:
- `draft.trim()` is non-empty.
- `draft.trim() !== value` (value actually changed).

If the user clears the input and blurs/confirms, no callback fires (empty
draft is treated as cancel).

### 3.3 Draft lifecycle

The draft is reset from `value` every time editing begins (via `useEffect`
on `editing` changing to `true`). The draft is discarded on Escape.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onConfirm` | `string` (trimmed draft) | Edit committed via blur or Enter, if value changed and non-empty. |

---

## 5. Variants and states

| State | Visual |
|---|---|
| **View** | `<button>` with value text + hidden pencil icon |
| **View (empty value)** | Placeholder text in italic + muted style |
| **Edit** | `<input>` with current draft |

---

## 6. Composition

InlineEdit is self-contained. It manages its own edit mode state. It does not
integrate with FormField or FieldWrapper.

---

## 7. Deferred features

- **Multi-line editing** (textarea variant).
- **Validation / error state** in edit mode.
- **Confirm / cancel button pair** instead of implicit blur/Enter confirm.
