# DocumentUploadZone — Styling Contract

- **Component:** DocumentUploadZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DocumentUploadZone.Semantic.md) · [Interaction](./DocumentUploadZone.Interaction.md) · [Accessibility](./DocumentUploadZone.Accessibility.md) · [Styling](./DocumentUploadZone.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DocumentUploadZone.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

DocumentUploadZone has three visual zones: the drop target, the drag-active
overlay, and the uploaded-file list. This contract names the Tailwind recipes
for each state.

---

## 2. Drop zone base recipe

```
flex flex-col items-center justify-center gap-2
rounded-xl border-2 border-dashed px-6 py-8
transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

---

## 3. Drop zone state overlays

| State | Condition | Recipe |
|---|---|---|
| **Idle** | enabled, no drag | `border-gray-300 bg-white hover:border-blue-300 hover:bg-gray-50 cursor-pointer` |
| **Dragging** | drag active over zone | `border-blue-400 bg-blue-50 cursor-copy` |
| **Disabled** | `disabled === true` | `bg-gray-50 border-gray-200 cursor-not-allowed` |

The instruction text flips from `'Drag & drop or click to upload'` to
`'Drop to upload'` during drag (state-driven text change, not a CSS class).

---

## 4. Uploaded file list item recipe

```
flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2
```

Remove button:
```
rounded-md p-1 text-gray-400 hover:bg-red-50 hover:text-red-500 focus:outline-none shrink-0
```

---

## 5. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | drop zone border | `border-gray-300 border-dashed` |
| idle hover | drop zone border + bg | `hover:border-blue-300 hover:bg-gray-50` |
| dragging | drop zone border + bg | `border-blue-400 bg-blue-50` |
| disabled | drop zone bg + border | `bg-gray-50 border-gray-200 cursor-not-allowed` |
| focus-visible | drop zone ring | `focus-visible:ring-2 focus-visible:ring-blue-500` |

---

## 6. Known gaps

- No size variants — padding and text size are hard-coded.
- Remove button removes its focus ring entirely with `focus:outline-none` (accessibility gap; see Accessibility G1).
- No token family defined for DocumentUploadZone; `--sf-upload-*` tokens are
  deferred to M2.
