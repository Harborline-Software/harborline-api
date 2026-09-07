# ActionMenu — Styling Contract

- **Component:** ActionMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionMenu.Semantic.md) · [Interaction](./ActionMenu.Interaction.md) · [Accessibility](./ActionMenu.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/ActionMenu.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`relative inline-block` + `className` passthrough.

---

## 2. Default trigger button

`p-1 rounded hover:bg-gray-100 text-gray-500`

SVG icon: `h-5 w-5` (3-dot horizontal).

---

## 3. Dropdown panel

`absolute z-50 mt-1 min-w-[10rem] rounded-md border border-gray-200 bg-white py-1 shadow-lg`

Alignment: `align='right'` → `right-0`; `align='left'` → `left-0`.

---

## 4. Menu items

Base: `flex w-full items-center gap-2 px-3 py-2 text-sm text-left`

Default variant: `text-gray-700 hover:bg-gray-50`

Destructive variant: `text-red-600 hover:bg-red-50`

Disabled: `opacity-50 cursor-not-allowed`

Icon slot: `h-4 w-4 shrink-0`

---

## 5. Separator

`<hr className="my-1 border-gray-100">`

---

## 6. Design token deviation

All colors are hardcoded (`gray-*`, `red-*`). M2 will replace with design tokens (`border`, `background`, `foreground`, `muted`, `destructive`).
