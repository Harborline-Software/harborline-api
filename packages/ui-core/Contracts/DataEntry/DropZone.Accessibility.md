# DropZone — Accessibility Contract

- **Component:** DropZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropZone.Semantic.md) · [Interaction](./DropZone.Interaction.md) · [Styling](./DropZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropZone.tsx`
- **Catalog row:** #50 DropZone (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="button"` | Zone `<div>` | Makes the drop zone a button for AT |
| `tabIndex={0}` | Zone `<div>` | Keyboard focusable when enabled |
| `tabIndex={-1}` | Zone `<div>` | Not focusable when disabled |
| `aria-disabled={disabled}` | Zone `<div>` | Communicates disabled state |
| `aria-label="Drop files here or click to browse"` | Zone `<div>` | Names the interactive region |
| `role="alert"` | Error `<p>` | Announces errors to AT immediately |
| `role="status"` | Accepted-file `<p>` | Polite, atomic announcement of the accepted-file count |
| `<input type="file">` | sr-only input | `tabIndex={-1}`; not in AT focus order |

---

## 2. File upload via keyboard

Keyboard users can focus the zone (`Tab`) and press `Enter` or `Space` to open the native file dialog. The file dialog itself is native OS UI with its own accessibility support.

---

## 3. Error announcement

`<p role="alert">` ensures AT immediately announces validation errors (e.g. file-too-large) without focus change.

## 4. Accepted-file announcement

After a valid file selection, a screen-reader-only `role="status"` announces the accepted-file
count politely. `aria-atomic="true"` confines each update to that selection sentence, avoiding
re-announcement of a host's selected-file list.

## 5. Known gaps

None.
