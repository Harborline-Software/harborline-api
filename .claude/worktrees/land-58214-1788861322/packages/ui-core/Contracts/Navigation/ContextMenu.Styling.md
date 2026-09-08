# ContextMenu — Styling Contract

- **Component:** ContextMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ContextMenu.Semantic.md) · [Interaction](./ContextMenu.Interaction.md) · [Accessibility](./ContextMenu.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ContextMenu.tsx`
- **Catalog row:** #34 ContextMenu (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper div

`className` passthrough — no intrinsic classes. Wraps children for `onContextMenu` capture.

---

## 2. Menu panel

`position: fixed; top: pos.y; left: pos.x; z-index: 9999`
`min-w-[160px] rounded-lg border border-gray-200 bg-white py-1 shadow-lg focus:outline-none`

> **M1 note:** Uses hardcoded `bg-white` and `border-gray-200` rather than design tokens.

---

## 3. Group separator

`my-1 border-t border-gray-100`

---

## 4. Item button — base

`w-full flex items-center gap-2 px-3 py-1.5 text-sm text-left`
`transition-colors focus:outline-none`
`cursor-pointer` (enabled) / `cursor-not-allowed opacity-50` (disabled)

---

## 5. Item button — variants

| Variant | Normal | Hover / Focus | Active (keyboard) |
|---|---|---|---|
| Default | `text-gray-700` | `hover:bg-gray-50 focus:bg-gray-50` | `bg-gray-50` |
| Danger | `text-red-600` | `hover:bg-red-50 focus:bg-red-50` | `bg-red-50` |

---

## 6. Icon

`text-gray-400 w-4 h-4 flex items-center justify-center shrink-0`
