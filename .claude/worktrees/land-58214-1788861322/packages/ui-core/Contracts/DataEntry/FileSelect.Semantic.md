# FileSelect — Semantic Contract

- **Component:** FileSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FileSelect.Interaction.md) · [Accessibility](./FileSelect.Accessibility.md) · [Styling](./FileSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input type="file">` variant

---

## 1. Purpose

FileSelect is a minimal file-picker button. It renders a styled `<button>`
that opens the OS file picker via a hidden `<input type="file">`. It is
intentionally simpler than DocumentUploadZone — no drop zone, no file list,
no drag-and-drop. Its role is to provide a styled entry point for file
selection, typically in a toolbar or inline context.

---

## 2. Data model

FileSelect is callback-based. The host receives selected files via `onSelect`.

```typescript
interface FileSelectProps {
  onSelect?: (files: File[]) => void
  accept?: string
  multiple?: boolean
  maxFileSize?: number    // bytes
  disabled?: boolean
  text?: string
  size?: 'small' | 'medium' | 'large'
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `onSelect` | `(files: File[]) => void` | — | Called with filtered `File[]` after user selects files. Not called if no files pass the size filter. |
| `accept` | `string` | — | Passed directly to the hidden `<input accept>`. Any valid MIME / extension string. |
| `multiple` | `boolean` | `false` | Whether multiple files can be selected. |
| `maxFileSize` | `number` | — | Maximum file size in bytes. Files exceeding this are filtered before `onSelect`. No error is raised for over-limit files. |
| `disabled` | `boolean` | `false` | When `true`, the button is non-interactive. |
| `text` | `string` | `'Select files…'` | Visible button label. |
| `size` | `'small' \| 'medium' \| 'large'` | `'medium'` | Button height and text size variant. |
| `className` | `string` | — | Additional CSS classes on the button. |

### 3.1 Size axis

| Size | Height class | Padding | Font |
|---|---|---|---|
| `small` | `h-7` | `px-2.5` | `text-xs` |
| `medium` | `h-9` | `px-3` | `text-sm` |
| `large` | `h-11` | `px-4` | `text-base` |

### 3.2 File size filtering

After selection, files are filtered: `files.filter(f => !maxFileSize || f.size <= maxFileSize)`. Over-limit files are silently dropped. The hidden input value is reset after each selection so the same file can be re-selected.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onSelect` | `File[]` | User selects files and at least one passes the size filter. |

---

## 5. Composition

FileSelect renders a `<>` fragment containing the hidden `<input>` and the
visible `<button>`. It does not wrap in a `<div>` — the host controls layout.

---

## 6. Deferred features

- **Drop zone.** FileSelect is button-only; use DocumentUploadZone for drag-drop.
- **Error display for over-limit files.**
- **Progress state or upload feedback.**
