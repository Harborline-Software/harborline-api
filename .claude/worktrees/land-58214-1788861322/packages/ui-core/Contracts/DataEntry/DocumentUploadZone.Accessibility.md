# DocumentUploadZone — Accessibility Contract

- **Component:** DocumentUploadZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DocumentUploadZone.Semantic.md) · [Interaction](./DocumentUploadZone.Interaction.md) · [Accessibility](./DocumentUploadZone.Accessibility.md) · [Styling](./DocumentUploadZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DocumentUploadZone.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

DocumentUploadZone presents a drag-and-drop target as a keyboard-accessible
button. The hidden native `<input type="file">` is excluded from the AT tree;
interaction is routed through the `role="button"` drop zone element.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Drop zone `<div>` | `role="button"` | Explicit; with `tabIndex={0}` when enabled |
| `<input type="file">` | — | `aria-hidden="true"`, `tabIndex={-1}` — excluded from AT tree |
| Uploaded files `<ul>` | `list` (implicit) | `role="list"` and `aria-label="Uploaded files"` |
| Remove `<button>` | `button` | `aria-label="Remove {filename}"` |
| Cloud icon `<span>` | decorative | `aria-hidden="true"` |
| File type icon `<span>` | decorative | `aria-hidden="true"` |

---

## 3. Drop zone label

```tsx
aria-label={`Upload ${multiple ? 'files' : 'a file'}`}
```

AT reads: "Upload files, button" (or "Upload a file, button"). The visual
instruction text ("Drag & drop or click to upload") is supplementary.

**WCAG citations:**
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 1.3.1 Info and Relationships

---

## 4. Keyboard navigation

| Key | Behaviour |
|---|---|
| Tab | Focus the drop zone (when enabled). |
| Enter | Opens OS file picker. |
| Space | Opens OS file picker. |
| Tab (on remove button) | Focus moves to the remove button for the first uploaded file (then Tab through remaining). |
| Enter / Space (on remove button) | Removes the file at that index. |

The drop zone element itself does not use native button semantics — it uses
`role="button"` on a `<div>`. The Enter/Space activation is custom-implemented
via `onKeyDown`. This is correct WAI-ARIA authoring practice for drag targets
that also need to be clickable areas (a `<button>` would not accept `onDrop`
semantically).

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap

---

## 5. Focus visible

The drop zone has:
```
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

Remove buttons have:
```
focus:outline-none
```

Known gap: the remove button suppresses the focus ring entirely (`focus:outline-
none` with no replacement ring). Keyboard users cannot see focus on remove
buttons.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible.

---

## 6. Uploaded file list

The file list is a `<ul role="list" aria-label="Uploaded files">`. Each `<li>`
contains:
- Decorative file-type icon (`aria-hidden="true"`)
- File name (visible text)
- Formatted file size (visible text)
- Remove button with `aria-label="Remove {filename}"` (when `onRemoveFile` provided)

AT reads each item as: "{filename} {size} Remove {filename} button".

---

## 7. Disabled state

When `disabled === true`:

- `tabIndex={-1}` removes the drop zone from the Tab order.
- The hidden `<input type="file">` has `disabled` set.
- No content-change announcement is needed (visual disabled state is clear to
  sighted users; AT users encounter the element as unfocusable).

---

## 8. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Remove buttons have `focus:outline-none` with no replacement ring | High | Add `focus-visible:ring-2 focus-visible:ring-blue-500` to remove button |
| G2 | No live region announces accepted/rejected files after drop | Medium | Add `aria-live="polite"` region with file count summary |
| G3 | Dragging state change not announced to AT | Low | Add `aria-live="polite"` or `aria-describedby` update on drag state |
| G4 | Over-limit files are silently filtered — no AT feedback | Medium | Requires error callback + live region for rejected-file count |
