# ImageUploader — Semantic Contract

- **Component:** ImageUploader
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ImageUploader.Interaction.md) · [Accessibility](./ImageUploader.Accessibility.md) · [Styling](./ImageUploader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ImageUploader.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — extends Upload (native `<input type="file">`)

---

## 1. Purpose

ImageUploader is a single-image upload control that supports click-to-browse
and drag-and-drop. When an image is selected, it is read as a data URL via
`FileReader` and the result is passed to `onChange`. The component displays a
live preview of the selected image, with an inline remove button. An internal
error state handles file-size violations.

---

## 2. Data model

ImageUploader is partially controlled: the host can supply `value` (a data URL
or image URL string) and receives updates via `onChange`. Internal state tracks
the drag-over flag and the error message.

```typescript
interface ImageUploaderProps {
  value?: string | null
  defaultValue?: string
  onChange?: (value: string | null) => void
  accept?: string
  maxSizeMB?: number
  placeholder?: string
  disabled?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string \| null` | — | Controlled image value (data URL or image URL). When provided and non-null, displays the preview. |
| `defaultValue` | `string` | — | Uncontrolled initial value. Note: `defaultValue` is declared in the interface but not consumed by the implementation in M1 — the component always operates from `value`. |
| `onChange` | `(value: string \| null) => void` | — | Called with the new data URL after a file is read, or `null` when the image is cleared. |
| `accept` | `string` | `'image/*'` | Passed to the hidden `<input accept>`. |
| `maxSizeMB` | `number` | `5` | Maximum file size in megabytes. Files exceeding this set the internal error state. |
| `placeholder` | `string` | `'Click or drag to upload image'` | Text shown in the empty state. |
| `disabled` | `boolean` | `false` | When `true`, all interactions are disabled. |
| `className` | `string` | `''` | Additional CSS classes on the root wrapper. |

### 3.1 Value vs defaultValue

The `defaultValue` prop is defined in the interface but not wired in the
implementation — the render path only branches on `value` (controlled). This
is a known gap: uncontrolled usage is not supported in M1.

### 3.2 Internal error state

When a file exceeds `maxSizeMB`, the component sets an internal `error:
string` state and displays it below the uploader as `<p className="text-xs
text-red-600">`. This is the only error path — type errors (e.g. non-image
file dropped when `accept='image/*'`) are not validated.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `string` | FileReader reads a valid-size file and produces a data URL. |
| `onChange` | `null` | User clicks the remove button. |

---

## 5. Variants and states

| State | Trigger |
|---|---|
| **Empty** | `value` is null / undefined |
| **Preview** | `value` is a non-null string |
| **Dragging** | Drag is active over the zone (internal state) |
| **Disabled** | `disabled === true` |
| **Error** | File size exceeded (internal error state) |

---

## 6. Composition

ImageUploader is self-contained. It does not integrate with FormField or
FieldWrapper.

---

## 7. Deferred features

- **Uncontrolled mode.** `defaultValue` is declared but not implemented.
- **Multiple images.** Only single-image capture.
- **Crop / resize tools.**
- **Type validation error.** Non-image files are not rejected with feedback.
