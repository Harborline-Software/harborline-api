# DataExportButton — Styling Contract

- **Component:** DataExportButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataExportButton.Semantic.md) · [Interaction](./DataExportButton.Interaction.md) · [Accessibility](./DataExportButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DataExportButton.tsx`
- **Catalog row:** #A-DEX DataExportButton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Button (both modes)

`flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50`

---

## 2. Loading spinner

`h-4 w-4 rounded-full border-2 border-gray-300 border-t-blue-500 animate-spin` (CSS spinner, not SVG)

---

## 3. Multi-format container

`relative inline-block` + `className` passthrough.

---

## 4. Dropdown caret indicator

Inline text `▾` with `text-gray-400 text-xs ml-0.5` — displayed only in multi-format mode.

---

## 5. Dropdown panel

`absolute right-0 top-full mt-1 z-50 min-w-[160px] rounded-xl border border-gray-200 bg-white py-1 shadow-lg`

---

## 6. Format menu items

`w-full flex items-center gap-2 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors focus:outline-none focus-visible:bg-gray-50 text-left`

---

## 7. Design token deviation

All colors are hardcoded (`gray-300`, `gray-700`, `gray-50`, `blue-500`, `gray-400`, `gray-200`). M2 will replace with design tokens.
