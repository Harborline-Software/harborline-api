# DropZone — Semantic Contract

- **Component:** DropZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DropZone.Interaction.md) · [Accessibility](./DropZone.Accessibility.md) · [Styling](./DropZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropZone.tsx`
- **Catalog row:** #50 DropZone (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<div>` drag-and-drop zone

---

## 1. Component purpose

**DropZone** — a drag-and-drop file drop target that also supports click-to-browse. Validates file sizes, emits accepted files via `onFiles`, and shows an error if files exceed `maxSize`. Supports custom content via `children` or falls back to a built-in prompt.

---

## 2. Props

```typescript
interface DropZoneProps extends Omit<React.HTMLAttributes<HTMLDivElement>, 'onDrop'> {
  onFiles?: (files: File[]) => void   // called with accepted files
  accept?: string                     // MIME types / extensions for file input
  multiple?: boolean                  // default: true
  maxSize?: number                    // max file size in bytes
  disabled?: boolean                  // default: false
  children?: React.ReactNode          // custom content; replaces default prompt
}
```

---

## 3. States

| State | Condition |
|---|---|
| Default | No drag, no error |
| Drag-over | `isDragOver=true` (pointer entering the zone) |
| Error | A dropped/selected file exceeded `maxSize` |
| Disabled | `disabled=true` |

---

## 4. Error handling

Only `maxSize` is validated in M1. MIME-type filtering via `accept` is enforced by the native file input only (no JS-side MIME check). Error message: `"{n} file(s) exceed the {limit}MB limit"` — displayed via `<p role="alert">`.

---

## 5. Custom content

`children` replaces the built-in icon + text. The drag-over and error states still apply to the container regardless of custom children.

---

## 6. Wave-N expansion — CG-12 uploadRef linkage (2026-06-12)

> Wave tag: **wave-N** (implemented; spec promoted to Accepted on PR open).

```typescript
// Addition to DropZoneProps
uploadRef?: React.RefObject<HTMLInputElement | null>
```

**`uploadRef`** — when provided, the ref's `.current` is set to the internal hidden `<input type="file">` element after mount. This allows the host to programmatically open the native file picker dialog without relying on simulated click events on the DropZone container.

**Use case:** pairing DropZone with an Upload component — the host passes the same `uploadRef` to both so that clicking a custom "Browse files" button in the Upload action bar triggers the DropZone's file picker.

**Implementation:** a `useEffect` (no deps, runs every render) assigns `inputRef.current` to `uploadRef.current`. This ensures the ref is populated even if the component re-renders before the caller reads it.

**TypeScript:** `React.RefObject<HTMLInputElement | null>` — the type matches `React.createRef<HTMLInputElement>()` return type.
