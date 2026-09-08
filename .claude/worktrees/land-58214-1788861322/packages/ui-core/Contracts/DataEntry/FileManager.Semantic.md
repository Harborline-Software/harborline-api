# FileManager — Semantic Contract

- **Component:** FileManager
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FileManager.Interaction.md) · [Accessibility](./FileManager.Accessibility.md) · [Styling](./FileManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileManager.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled file browser UI

---

## 1. Purpose

FileManager is a hierarchical file-browser widget. It renders a toolbar with
breadcrumb navigation, a file/folder list in either list or grid view mode,
and supports single/multi-selection, rename-in-place, delete, upload, and
new-folder operations. The host owns the flat `data` array; FileManager
derives the current folder view internally.

---

## 2. Data model

```typescript
interface FileManagerEntry {
  id: string
  name: string
  type: 'file' | 'folder'
  size?: number          // bytes
  modified?: Date
  parentId?: string | null
  path?: string          // optional path string for navigation callback
}

interface FileManagerProps {
  data: FileManagerEntry[]
  onNavigate?: (path: string) => void
  onFileOpen?: (entry: FileManagerEntry) => void
  onRename?: (entry: FileManagerEntry, newName: string) => void
  onDelete?: (entries: FileManagerEntry[]) => void
  onUpload?: (files: File[], parentId: string | null) => void
  onNewFolder?: (name: string, parentId: string | null) => void
  view?: 'grid' | 'list'
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `data` | `FileManagerEntry[]` | _required_ | Flat array of all entries. FileManager filters to `parentId === currentFolder` to build the current view. |
| `onNavigate` | `(path: string) => void` | — | Called when a folder is opened. Receives `entry.path ?? entry.name`. |
| `onFileOpen` | `(entry: FileManagerEntry) => void` | — | Called when a file (type `'file'`) is double-clicked. |
| `onRename` | `(entry, newName) => void` | — | Called when rename is confirmed via blur or Enter. Only fires when `newName !== entry.name` and `newName` is non-empty. |
| `onDelete` | `(entries: FileManagerEntry[]) => void` | — | Called when the Delete toolbar button is clicked. Receives the currently selected entries. |
| `onUpload` | `(files, parentId) => void` | — | Called when files are selected via the Upload button. Receives raw `File[]` and the current folder's id (or `null` for root). |
| `onNewFolder` | `(name, parentId) => void` | — | Called when the user confirms a new folder name via `window.prompt`. |
| `view` | `'grid' \| 'list'` | `'list'` | Initial view mode. User can toggle between modes via the toolbar. |
| `className` | `string` | — | Additional CSS classes on the root container. |

### 3.1 Folder traversal (internal state)

- `currentFolder: string | null` — id of the currently displayed folder
  (`null` = root).
- FileManager builds the current view by filtering `data` to entries where
  `parentId === currentFolder`.
- Folders render before files within the view (sorted by type).

### 3.2 Breadcrumb derivation

FileManager walks up the parent chain from `currentFolder` to root, building
the `breadcrumbs` array. Each crumb renders as a clickable button.

### 3.3 New folder via `window.prompt`

`handleNewFolder` calls `window.prompt('Folder name:')`. This is a browser
modal — it blocks the tab. Known gap: `window.prompt` is not accessible, is
blocked in some security contexts, and gives no UI control over styling.

---

## 4. Internal state

| State variable | Type | Default | Meaning |
|---|---|---|---|
| `currentFolder` | `string \| null` | `null` | Currently displayed folder id |
| `selected` | `Set<string>` | `new Set()` | Selected entry ids |
| `renamingId` | `string \| null` | `null` | Id of entry currently being renamed |
| `renameValue` | `string` | `''` | Current draft name during rename |
| `viewMode` | `'grid' \| 'list'` | `view` prop | Current display mode |

---

## 5. Events

| Event | Fired when | Payload |
|---|---|---|
| `onNavigate` | Folder is opened | `entry.path ?? entry.name` |
| `onFileOpen` | File is double-clicked | `FileManagerEntry` |
| `onRename` | Rename confirmed | `(entry, newName: string)` |
| `onDelete` | Delete button clicked with selection | `FileManagerEntry[]` |
| `onUpload` | Files selected via Upload button | `(File[], parentId: string \| null)` |
| `onNewFolder` | User confirms folder name in prompt | `(name: string, parentId: string \| null)` |

---

## 6. Deferred features

- **Context menu (right-click).** Only single/multi-select + toolbar actions.
- **Drag-and-drop for reordering / moving** entries between folders.
- **Sort by column** (name / size / modified).
- **File preview panel.**
- **`window.prompt` replacement with inline input or modal dialog.**
