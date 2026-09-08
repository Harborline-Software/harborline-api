# DropZone — Styling Contract

- **Component:** DropZone
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropZone.Semantic.md) · [Interaction](./DropZone.Interaction.md) · [Accessibility](./DropZone.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropZone.tsx`
- **Catalog row:** #50 DropZone (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Zone container

Base: `flex cursor-pointer flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-10 text-center transition-colors`

| State | Classes |
|---|---|
| Default | `border-gray-300 bg-white hover:border-gray-400 hover:bg-gray-50` |
| Drag-over | `border-blue-400 bg-blue-50` |
| Error | `border-red-400 bg-red-50` |
| Disabled | `cursor-not-allowed opacity-50 hover:border-gray-300 hover:bg-white` (hover overrides removed) |

---

## 2. Hidden file input

`sr-only` (visually hidden, remains in DOM for programmatic `.click()`).

---

## 3. Default content (when no `children`)

Upload icon SVG: `mb-3 h-10 w-10 text-gray-400`
Primary text: `text-sm font-medium text-gray-700`
Accept hint: `mt-1 text-xs text-gray-500`
Max size hint: `mt-1 text-xs text-gray-500`

---

## 4. Error message

`mt-2 text-sm text-red-600` with `role="alert"`
