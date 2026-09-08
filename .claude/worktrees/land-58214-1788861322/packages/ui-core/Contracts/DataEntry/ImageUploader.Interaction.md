# ImageUploader — Interaction Contract

- **Component:** ImageUploader
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ImageUploader.Semantic.md) · [Interaction](./ImageUploader.Interaction.md) · [Accessibility](./ImageUploader.Accessibility.md) · [Styling](./ImageUploader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ImageUploader.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers click-to-browse, drag-and-drop, file-size validation,
FileReader loading, preview display, image clearing, and the disabled mode.

---

## 2. Click-to-browse

When the drop zone `<div role="button">` is clicked and not disabled:
`inputRef.current?.click()` fires the OS image picker. The hidden input resets
to `''` after selection so the same file can be re-selected.

---

## 3. Drag-and-drop

| Event | Behaviour |
|---|---|
| `dragover` | `e.preventDefault()`, sets `dragging = true` (when not disabled). |
| `dragleave` | Sets `dragging = false` when pointer leaves the zone entirely. |
| `drop` | `e.preventDefault()`, sets `dragging = false`, calls `handleFile(e.dataTransfer.files?.[0])`. First file only — multi-file drop picks first file. |

---

## 4. Keyboard activation

The drop zone has `role="button"` with `tabIndex={0}` when enabled.
Key handler: `Enter` or `Space` → `inputRef.current?.click()`.

---

## 5. File handling — `handleFile(file)`

1. Clears any prior internal error: `setError(null)`.
2. Checks size: if `file.size > maxSizeMB * 1024 * 1024`:
   - Sets internal error: `setError("File must be under {maxSizeMB} MB")`.
   - Returns — does NOT read the file.
3. Creates a `FileReader`, reads the file as data URL.
4. On `reader.onload`: calls `onChange?.(e.target?.result as string)`.

The component does NOT suppress the hidden `<input>`'s file change or validate
against `accept` client-side — the OS picker and the native `accept` attribute
handle type restriction.

---

## 6. Image preview + remove

When `value` is non-null:

- Renders `<img src={value} alt="Uploaded preview" className="w-full h-40 object-cover">`.
- Renders a "✕" overlay button (`aria-label="Remove image"`) in the top-right.
- Clicking the remove button calls `e.stopPropagation()`, calls `onChange?.(null)`,
  resets the hidden input value.

---

## 7. Disabled mode

When `disabled === true`:

- Drop zone `tabIndex = -1` (unfocusable).
- Click does not open file picker.
- Drag events are ignored (`if (disabled) return` inside `handleDrop` and
  `handleDragOver`).
- Remove button is NOT rendered when disabled (so `value` is locked).
- Visual: `cursor-not-allowed opacity-50 bg-gray-50`.

---

## 8. State transitions

```
Empty ──click/Enter──► picker ──select──► FileReader ──onload──► Preview (onChange(dataUrl))
Empty ──drop──► FileReader ──onload──► Preview
Empty ──drop oversized──► Error state (no onChange)
Preview ──click remove──► Empty (onChange(null))
```

---

## 9. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | `defaultValue` declared but not implemented — uncontrolled mode not supported | Host must always supply `value` |
| G2 | Multi-file drag-drop silently uses only the first file | User expectation mismatch |
| G3 | No file-type validation beyond the `accept` attribute hint | Non-image files can be processed if the user bypasses the picker |
