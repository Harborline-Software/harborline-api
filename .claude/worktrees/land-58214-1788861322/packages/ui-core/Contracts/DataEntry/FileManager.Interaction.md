# FileManager — Interaction Contract

- **Component:** FileManager
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileManager.Semantic.md) · [Interaction](./FileManager.Interaction.md) · [Accessibility](./FileManager.Accessibility.md) · [Styling](./FileManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileManager.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers navigation, selection, rename, delete, upload, new-folder,
and view-mode toggle interactions.

---

## 2. Navigation

- **Home breadcrumb button:** sets `currentFolder = null`, clears selection.
- **Breadcrumb segment button:** sets `currentFolder` to that entry's id,
  clears selection.
- **Double-click on a folder row/cell:** opens the folder (`currentFolder =
  entry.id`), clears selection, calls `onNavigate?.(entry.path ?? entry.name)`.
- **Double-click on a file row/cell:** calls `onFileOpen?.(entry)`.

---

## 3. Selection

### Single-click
Sets the selection to `{ entry.id }` (replaces any prior selection) unless
the same entry was already selected and it's the only selected item (in which
case the set is cleared via `next.delete(id)` when `!multi`).

### Multi-select (Ctrl/Cmd + click)
When `e.metaKey || e.ctrlKey`, adds the clicked entry to the selection
without clearing others. Clicking an already-selected entry while holding
Ctrl/Cmd de-selects it.

---

## 4. Rename

Rename is available only in list view, only for a single selected entry,
and only when `onRename` is provided.

1. A "Rename" button appears inline next to the selected entry's name.
2. Clicking it sets `renamingId = entry.id` and `renameValue = entry.name`,
   replacing the name text with an `<input autoFocus>`.
3. The rename input is committed via:
   - **Blur** — calls `commitRename(entry)`.
   - **Enter key** — calls `commitRename(entry)`.
4. **Escape** cancels rename (`setRenamingId(null)`) — the draft is discarded.
5. `commitRename` calls `onRename(entry, renameValue.trim())` only if the
   trimmed draft is non-empty AND differs from the original name. It always
   clears `renamingId`.

---

## 5. Delete

- The "Delete (N)" toolbar button appears only when `selected.size > 0` AND
  `onDelete` is provided.
- On click: calls `onDelete(items.filter(e => selected.has(e.id)))`, then
  clears selection.
- No confirmation dialog in M1 — the host is responsible for confirmation UX
  if needed.

---

## 6. Upload

- The "Upload" toolbar button is shown only when `onUpload` is provided.
- On click: programmatically clicks a hidden `<input type="file" multiple>`.
- On file selection: calls `onUpload(files, currentFolder)`. Resets the file
  input value after each selection.

---

## 7. New folder

- The "+ Folder" toolbar button is shown only when `onNewFolder` is provided.
- On click: calls `window.prompt('Folder name:')`. If the user enters a
  non-empty name, calls `onNewFolder(name.trim(), currentFolder)`.
- Known gap: `window.prompt` is a browser modal — no custom styling, blocked
  in some security contexts, not accessible in all AT configurations.

---

## 8. View mode toggle

The toolbar button cycles between `'list'` and `'grid'` view. The button label
shows the OTHER mode to switch to ("Grid" when currently showing List, "List"
when showing Grid).

---

## 9. Keyboard behaviour

The current implementation does not add custom keyboard handlers to rows or
grid cells. Navigation, open, and selection are mouse-driven in M1.
**This is a significant accessibility gap** (see Accessibility G1).

---

## 10. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | No keyboard navigation on rows / cells — Tab/Enter/Space not handled | Keyboard-only users cannot navigate the file list |
| G2 | `window.prompt` for new folder name — not accessible, not stylable, blocked in CSP | Replace with inline input or dialog |
| G3 | No delete confirmation dialog | Accidental deletes cannot be undone |
| G4 | Rename only available in list view, not grid view | Inconsistent UX |
| G5 | Multi-select only via Ctrl/Cmd+click — no Shift+click range selection | Standard file browser feature missing |
