# DocumentUploadZone — Semantic Contract

- **Component:** DocumentUploadZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DocumentUploadZone.Interaction.md) · [Accessibility](./DocumentUploadZone.Accessibility.md) · [Styling](./DocumentUploadZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DocumentUploadZone.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — extends Upload (native `<input type="file">`)

---

## 1. Purpose

DocumentUploadZone is a drag-and-drop + click-to-upload zone for document and
file selection. It renders a bordered drop target, manages a hidden `<input
type="file">`, and optionally displays a list of already-uploaded files with
per-file remove controls. File-size filtering is built in; type filtering is
configurable.

---

## 2. Data model

DocumentUploadZone is callback-based — it does not own a file list. The host
owns the `uploadedFiles` list and receives new files via `onFiles`.

```typescript
type DocumentUploadAccept = 'all' | 'images' | 'pdf' | 'documents' | string

interface UploadedFile {
  name: string
  size: number       // bytes
  type: string       // MIME type
  preview?: string   // optional URL/data-URL for preview
}

interface DocumentUploadZoneProps {
  onFiles: (files: File[]) => void
  accept?: DocumentUploadAccept
  multiple?: boolean
  maxSizeMB?: number
  uploadedFiles?: UploadedFile[]
  onRemoveFile?: (index: number) => void
  disabled?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `onFiles` | `(files: File[]) => void` | _required_ | Called with the filtered (size-valid) `File[]` after user selects files via click or drag-drop. Never called with an empty array. |
| `accept` | `DocumentUploadAccept` | `'all'` | File type filter. Shorthand values map to MIME strings: `'all'`→`'*'`, `'images'`→`'image/*'`, `'pdf'`→`'application/pdf'`, `'documents'`→`'.pdf,.doc,.docx,.xls,.xlsx,.txt'`. Any other string is passed directly as the `accept` attribute. |
| `multiple` | `boolean` | `true` | Whether multiple files may be selected in one interaction. |
| `maxSizeMB` | `number` | `10` | Maximum file size in megabytes. Files exceeding this limit are silently filtered before `onFiles` is called. No error is raised for oversized files in M1. |
| `uploadedFiles` | `UploadedFile[]` | `[]` | List of already-uploaded files to display below the drop zone. The component renders this list but does not own it. |
| `onRemoveFile` | `(index: number) => void` | — | When provided, a remove button appears on each uploaded file row. Called with the file's index in `uploadedFiles`. |
| `disabled` | `boolean` | `false` | When `true`, the drop zone is non-interactive and styled as disabled. |
| `className` | `string` | `''` | Additional CSS classes on the root wrapper. |

### 3.1 Accept shorthand resolution

The component resolves `accept` at render time:

```
'all'       → '*'
'images'    → 'image/*'
'pdf'       → 'application/pdf'
'documents' → '.pdf,.doc,.docx,.xls,.xlsx,.txt'
custom str  → passed verbatim
```

### 3.2 File size filtering

Files are filtered client-side before `onFiles` is called:
`file.size <= maxSizeMB * 1_000_000`. Over-limit files are silently dropped.
No error callback or error state prop exists in M1 (known gap).

### 3.3 File list rendering

When `uploadedFiles.length > 0`, a `<ul role="list">` renders below the drop
zone. Each item shows: a type-based emoji icon (decorative, `aria-hidden`),
the file name (truncated), formatted file size, and optionally a remove button.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onFiles` | `File[]` | User selects files via click-to-browse or drag-and-drop. Only size-valid files included. |
| `onRemoveFile` | `number` (index) | User clicks the remove button on an uploaded file row. |

---

## 5. Variants and states

| State | Trigger |
|---|---|
| **Idle** | default |
| **Dragging** | a drag is active over the drop zone (`dragging === true` internal state) |
| **Disabled** | `disabled === true` |

---

## 6. Composition

DocumentUploadZone is self-contained. It does not integrate with `FormField`
or `FieldWrapper`. Hosts that want field-level label + error treatment must
wrap it themselves.

---

## 7. Deferred features

- **Over-limit file error callback / error display** — oversized files are
  silently filtered.
- **Upload progress / status** — no progress state; the component hands raw
  `File[]` to the host.
- **File preview images** — `UploadedFile.preview` is part of the data model
  but the component does not render a thumbnail in M1.
- **Drag-and-drop for the uploaded file list** (reorder).
