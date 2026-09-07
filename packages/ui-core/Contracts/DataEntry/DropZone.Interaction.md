# DropZone — Interaction Contract

- **Component:** DropZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropZone.Semantic.md) · [Accessibility](./DropZone.Accessibility.md) · [Styling](./DropZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropZone.tsx`
- **Catalog row:** #50 DropZone (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Drag-and-drop

| Event | Handler |
|---|---|
| `onDragOver` | `e.preventDefault()`; sets `isDragOver=true` if not disabled |
| `onDragLeave` | Clears `isDragOver` when pointer leaves zone (checks `relatedTarget` to avoid spurious child-element leave events) |
| `onDrop` | `e.preventDefault()`; clears drag-over; calls `processFiles(e.dataTransfer.files)` if not disabled |

---

## 2. Click-to-browse

`onClick` on the zone div: calls `inputRef.current.click()` to open the native file dialog.

`onKeyDown` on the zone: `Enter` or `Space` → triggers same click behavior.

`<input type="file">` is `sr-only` with `tabIndex={-1}` — not directly focusable, only triggered programmatically.

---

## 3. File input `onChange`

`handleInputChange`: calls `processFiles(e.target.files)`, then resets `e.target.value = ''` so the same file can be selected again.

---

## 4. `processFiles` logic

```
if (maxSize) check all files; if any oversized: setError(...); return
setError(null)
onFiles?.(files)
```

---

## 5. Keyboard

| Key | Behaviour |
|---|---|
| `Tab` | Zone receives focus (`tabIndex={0}` when not disabled; `-1` when disabled) |
| `Enter` / `Space` | Opens file browser dialog |

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DZ1 | Low | No JS-side MIME type validation — only native file input `accept` filter | Accepted-risk M1 |
