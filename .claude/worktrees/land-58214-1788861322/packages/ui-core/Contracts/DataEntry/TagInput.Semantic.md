# TagInput — Semantic Contract

- **Component:** TagInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TagInput.Interaction.md) · [Accessibility](./TagInput.Accessibility.md) · [Styling](./TagInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TagInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled tag chip input

---

## 1. Purpose

TagInput is a multi-value tag entry field. Tags are rendered as inline chips
inside the field area. Users add tags by typing and pressing Enter or comma;
they remove tags via per-tag remove buttons or by pressing Backspace on an
empty input to remove the last tag. It integrates with `FormFieldContext` for
id wiring.

---

## 2. Data model

TagInput is partially controlled: the host owns the `value` (tag array) and
receives updates via `onChange`. The pending text input is owned internally.

```typescript
interface TagInputProps {
  value?: string[]
  onChange?: (tags: string[]) => void
  placeholder?: string
  maxTags?: number
  label?: string
  error?: string
  hint?: string
  id?: string
  disabled?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string[]` | `[]` | Controlled array of current tags. |
| `onChange` | `(tags: string[]) => void` | — | Called when tags are added or removed. |
| `placeholder` | `string` | `'Add tag…'` | Placeholder in the text input. Hidden when `maxTags` reached. |
| `maxTags` | `number` | — | Maximum number of tags. Adding stops when `value.length >= maxTags`. |
| `label` | `string` | — | When provided, renders a `<label>` above the field area. |
| `error` | `string` | — | When provided, renders a `<p role="alert">` error below the field. Error border applied. |
| `hint` | `string` | — | When provided (and no error), renders a hint paragraph below the field. Wired to the input via `aria-describedby`. |
| `id` | `string` | — | Id for the text input. Falls back to `ctx?.id` from FormFieldContext. |
| `disabled` | `boolean` | — | When `true`, the field area receives `pointer-events-none opacity-60 bg-gray-50`. |
| `className` | `string` | — | Additional classes on the root wrapper. |

### 3.1 Duplicate prevention

Adding a tag that already exists in `value` is silently prevented.

### 3.2 Max tags enforcement

When `value.length >= maxTags`:
- The text input's `disabled` attribute is set (no more typing).
- The placeholder is hidden.

### 3.3 Blur-commit behaviour

On blur, if the text input has non-empty trimmed content, it is committed as
a tag (`addTag(input)`). This prevents partial tags being abandoned.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `string[]` | Tag added or removed. Always the full new array. |

---

## 5. Composition

TagInput integrates with `FormFieldContext` via `React.useContext`:

```typescript
const ctx = React.useContext(FormFieldContext)
const id = idProp ?? ctx?.id
```

It renders its own label, hint, and error — it does NOT rely on an outer
FormField for those.

---

## 6. Deferred features

- **Keyboard navigation within existing tags** (arrow left/right to focus tags).
- **Tag editing** (click a tag to edit its text).
- **Duplicate variants** (allow duplicates mode).
- **Max-tags visual limit indicator.**
