# Upload — Interaction Contract

- **Component:** Upload
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Upload.Semantic.md) · [Accessibility](./Upload.Accessibility.md) · [Styling](./Upload.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Upload.tsx`
- **Catalog row:** #144 Upload (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. File selection

"Select files" button → `inputRef.current?.click()` → triggers native file picker.

On file picker close: `handleSelect(e)`:
1. Filters files by `maxFileSize` (silent exclusion if exceeded)
2. Creates `UploadFile` objects with `status='selected'`
3. Appends to file list; calls `onFilesChange` and `onAdd`
4. Resets `inputRef.current.value = ''` (allows re-selecting same files)
5. If `saveUrl` exists: starts upload immediately

---

## 2. Upload trigger

"Upload" button (only shown when `saveUrl` is set): uploads all `status='selected'` files. Disabled when no 'selected' files exist.

---

## 3. Remove

Per-file remove button → `handleRemove(file)`:
1. Filters file out of list
2. Calls `onFilesChange` and `onRemove`
3. `removeUrl` is NOT called (gap — only local removal)

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-UPL1 | Medium | `removeUrl` prop is declared but never called — server-side deletion is not performed | Accepted-risk M1 |
| G-UPL2 | Medium | `chunkSize` prop is declared but not implemented — uploads always send full files | Accepted-risk M1 |
| G-UPL3 | Medium | `batch` prop path is unreachable — both branches call `handleUpload` immediately | Accepted-risk M1 |
| G-UPL4 | Medium | `maxFileSize` silently excludes oversized files — no user notification; no `onRejected` callback exists | Fix-in-M1: add `onRejected?: (files: File[], reason: 'maxFileSize' \| 'maxFiles' \| 'accept') => void` to Upload.Semantic.md props; default rendering shows a visible error per rejected file; AT announcement via `role="status"` live region |
