# Upload — Semantic Contract

- **Component:** Upload
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Upload.Interaction.md) · [Accessibility](./Upload.Accessibility.md) · [Styling](./Upload.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Upload.tsx`
- **Catalog row:** #144 Upload (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input type="file">` with hand-rolled file list

---

## 1. Component purpose

**Upload** — a file upload widget supporting single/multi-file selection, XHR upload with progress tracking, and per-file status management. Renders a "Select files" button, an optional "Upload" button, and a file list with progress and remove controls.

---

## 2. Props

```typescript
interface UploadFile {
  uid: string
  name: string
  size: number
  status: 'selected' | 'uploading' | 'success' | 'error'
  progress?: number
  error?: string
  rawFile?: File
}

interface UploadProps {
  files?: UploadFile[]
  defaultFiles?: UploadFile[]
  onFilesChange?: (files: UploadFile[]) => void
  saveUrl?: string          // POST endpoint; if absent, files stay 'selected' only
  removeUrl?: string        // declared but not called (M1 gap)
  onAdd?: (files: UploadFile[]) => void
  onRemove?: (file: UploadFile) => void
  onProgress?: (file: UploadFile, progress: number) => void
  onSuccess?: (file: UploadFile) => void
  onError?: (file: UploadFile, error: string) => void
  accept?: string           // MIME type filter, e.g. 'image/*'
  multiple?: boolean        // default: true
  maxFileSize?: number      // bytes; files exceeding this are silently excluded on select
  chunkSize?: number        // declared but not implemented
  batch?: boolean           // default: false — declared but both paths upload immediately
  withCredentials?: boolean // default: false — passed to XHR
  className?: string
}
```

---

## 3. Controlled / uncontrolled

Controlled when `files` is provided. Internal `files` state for uncontrolled. `onFilesChange` fires on every state change (add, remove, status update).

---

## 4. Upload flow

1. User selects files → `handleSelect` creates `UploadFile` objects with `status='selected'`
2. If `saveUrl` exists: immediately calls `handleUpload(file)` for each file
3. `handleUpload`: sets status='uploading', POSTs via XHR with progress events, sets status='success' or 'error'

---

## 5. uid generation

Module-level counter `uploadUid` incremented on each file. UIDs are `upload-N` (sequential, not UUIDs).

---

## 6. Wave-N expansion — CG-12 dispatchable gaps (2026-06-12)

> Wave tag: **wave-N** (all items implemented; spec promoted to Accepted on PR open).

### 6.1 Display toggles

```typescript
// Additions to UploadProps
autoUpload?: boolean        // default: true — when false, files are not auto-uploaded on select
showFileList?: boolean      // default: true — when false, the file list is hidden
showActionButtons?: boolean // default: true — when false, Select files + Upload buttons are hidden
```

- **`autoUpload`** — controls whether `handleUpload` fires automatically when files are selected (and `saveUrl` is set). When `false`, the Upload button remains visible so the user can manually trigger upload.
- **`showFileList`** — controls rendering of the `<ul>` file list below the action buttons. When `false`, files are still tracked in state but not displayed.
- **`showActionButtons`** — controls rendering of the entire action bar (Select files + Upload). Use when the host provides its own trigger UI or integrates with a DropZone.

### 6.2 Lifecycle interceptors

```typescript
onBeforeUpload?: (file: UploadFile) => boolean
onBeforeRemove?: (file: UploadFile) => boolean
onCancel?: (file: UploadFile) => void
```

- **`onBeforeUpload`** — called with the `UploadFile` object immediately before `handleUpload` is invoked (after `autoUpload` check). Return `false` to skip upload for that file; return `true` to proceed. Not called for files that are manually-triggered via the Upload button unless `onBeforeUpload` is also provided at that call site.
- **`onBeforeRemove`** — called with the `UploadFile` object when the user clicks the remove button. Return `false` to cancel removal (file remains in list); return `true` to proceed with removal.
- **`onCancel`** — when provided AND a file is in `status='uploading'`, a Cancel button renders beside the progress bar. Clicking it calls `onCancel(file)`. The component does not cancel the XHR automatically — the caller is responsible for aborting the in-flight request. The file status remains `'uploading'` until the caller updates it via the controlled `files` prop or the XHR resolves.

### 6.3 Restrictions typed object

```typescript
interface UploadRestrictions {
  allowedExtensions?: string[]  // e.g. ['.pdf', '.docx'] — case-insensitive match on filename
  maxFileSize?: number          // bytes; takes precedence over top-level maxFileSize prop
  minFileSize?: number          // bytes; files below this size are rejected
}

// Addition to UploadProps
restrictions?: UploadRestrictions
onRejected?: (files: File[], reason: 'maxFileSize' | 'minFileSize' | 'allowedExtensions') => void
```

- **`restrictions`** — typed replacement for the legacy `maxFileSize` prop. When `restrictions.maxFileSize` is set, it takes precedence. `restrictions.minFileSize` adds a new minimum size gate. `restrictions.allowedExtensions` filters by file extension (case-insensitive, dot-prefixed e.g. `'.pdf'`).
- **`onRejected`** — called once per rejection reason with the array of rejected `File` objects and the reason. A single `handleSelect` call may fire `onRejected` multiple times if multiple rejection reasons apply (e.g., one file too large AND another with wrong extension).
- The legacy `maxFileSize` prop is still accepted and applies its silent-exclusion behavior when `restrictions` is not provided.

### 6.4 Request headers

```typescript
saveHeaders?: Record<string, string>    // extra HTTP headers for upload requests
removeHeaders?: Record<string, string>  // extra HTTP headers for remove requests
```

- **`saveHeaders`** — key/value pairs forwarded to `xhr.setRequestHeader` before the upload POST. Common use: `{ Authorization: 'Bearer token' }`.
- **`removeHeaders`** — same for remove requests. Currently the component's `removeUrl` path uses `fetch()`; `removeHeaders` is accepted and stored but the `fetch()` call does not yet forward them (wave-N+ deferred, same as the existing `removeUrl` accepted-risk gap G-UPL1).

### 6.5 Upload flow update (wave-N)

Updated flow with wave-N interceptors:

1. User selects files → `applyRestrictions` filters by size/extension; `onRejected` fires per reason
2. Valid files added to list as `status='selected'`; `onAdd` fires
3. If `saveUrl` AND `autoUpload=true`: for each new file → `onBeforeUpload?.(file)` — skip if returns `false`; else → `handleUpload(file)` → sets `status='uploading'` → XHR POST with `saveHeaders`
4. While `status='uploading'` AND `onCancel` provided: Cancel button visible; click → `onCancel(file)` (XHR abort is caller responsibility)
5. XHR completes → `status='success'` or `status='error'`
6. User clicks remove → `onBeforeRemove?.(file)` — skip if returns `false`; else → file removed from list; `onRemove` fires; if `removeUrl` set → server-side remove called
