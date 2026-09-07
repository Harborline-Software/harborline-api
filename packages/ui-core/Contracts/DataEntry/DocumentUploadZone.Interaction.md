# DocumentUploadZone — Interaction Contract

- **Component:** DocumentUploadZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DocumentUploadZone.Semantic.md) · [Interaction](./DocumentUploadZone.Interaction.md) · [Accessibility](./DocumentUploadZone.Accessibility.md) · [Styling](./DocumentUploadZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DocumentUploadZone.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes the drag-and-drop interaction, click-to-browse
activation, keyboard activation, file-size filtering, and the disabled mode
of DocumentUploadZone.

---

## 2. Click-to-browse

When the drop zone is clicked and not disabled, `inputRef.current?.click()` is
called, opening the OS file picker. The hidden `<input type="file">` has
`tabIndex={-1}` and `aria-hidden`; it is never directly focused.

---

## 3. Drag-and-drop

| Event | Behaviour |
|---|---|
| `dragenter` | Sets `dragging = true` (shows active drop state). Ignored when disabled. |
| `dragover` | `e.preventDefault()` (allows drop). Sets `dragging = true`. Ignored when disabled. |
| `dragleave` | Sets `dragging = false` only when the pointer leaves the drop zone entirely (checked via `!currentTarget.contains(relatedTarget)`). |
| `drop` | `e.preventDefault()`, sets `dragging = false`, calls `handleFiles(e.dataTransfer.files)`. |

The dragging indicator changes both the drop zone's border colour and
background (visual state; see Styling contract).

---

## 4. File validation and filtering

`handleFiles(fileList)`:

1. Converts `FileList` to `File[]`.
2. Filters: keeps only files where `file.size <= maxSizeMB * 1_000_000`.
3. If the filtered array is non-empty, calls `onFiles(valid)`.
4. If all files are over-limit, `onFiles` is NOT called. No error is raised
   in M1 (silent drop — known gap G1).

---

## 5. Keyboard activation

The drop zone element has `role="button"` and `tabIndex={0}` when enabled,
`tabIndex={-1}` when disabled. Key handler:

| Key | Behaviour |
|---|---|
| `Enter` | Programmatically clicks the hidden file input (opens OS picker). |
| `Space` | Same as Enter. |

---

## 6. Remove file

When `onRemoveFile` is provided, each uploaded file row has a `<button
type="button">`. Clicking it calls `onRemoveFile(index)`. The host removes the
entry from `uploadedFiles`; the component re-renders.

---

## 7. Disabled mode

When `disabled === true`:

- The drop zone's `tabIndex` is set to `-1` (unfocusable).
- `dragenter`, `dragover` do not set `dragging` state.
- Click does not open the file picker.
- The `<input type="file">` has `disabled` set.
- Visual: `bg-gray-50 border-gray-200 cursor-not-allowed`.

---

## 8. State transitions

```
Idle ──click/Enter/Space──► OS picker open ──select──► onFiles(files)
Idle ──dragenter──► Dragging ──drop──► onFiles(files) ──► Idle
Idle ──dragenter──► Dragging ──dragleave──► Idle
```

---

## 9. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | No error callback / display for oversized files — silently filtered | User gets no feedback when all dropped files exceed the limit |
| G2 | No `onFilesRejected` callback for type-filtered files | Host can't know which files were rejected |
| G3 | `multiple={false}` only constrains the native picker, not drag-and-drop | Multiple files can still be dropped when `multiple={false}` |
