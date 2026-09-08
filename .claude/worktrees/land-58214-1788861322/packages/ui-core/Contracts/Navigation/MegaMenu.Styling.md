# MegaMenu — Styling Contract

- **Component:** MegaMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MegaMenu.Semantic.md) · [Interaction](./MegaMenu.Interaction.md) · [Accessibility](./MegaMenu.Accessibility.md) · [Styling](./MegaMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/MegaMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root wrapper

`relative` — anchors the absolute-positioned panel.

## 2. Trigger bar (`<nav>`)

`flex items-center gap-1`

## 3. Trigger button

Default: `flex items-center gap-1 rounded-md px-3 py-2 text-sm font-medium transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 text-gray-700 hover:bg-gray-50 hover:text-gray-900`

Active (open): `bg-gray-100 text-gray-900`

## 4. Panel

```
absolute left-0 top-full mt-1 z-50
min-w-[480px] rounded-xl border border-gray-200 bg-white shadow-lg overflow-hidden
```

- `z-50` — above page content.
- `min-w-[480px]` — minimum width ensures multi-column layout has room.

## 5. Column grid

`grid gap-0 p-4 grid-cols-N` where N = min(columns.length, 4).

Note: dynamic Tailwind class `grid-cols-${N}` requires safelist entries for N = 2, 3, 4.

## 6. Column separator

Columns after the first: `border-l border-gray-100 pl-4 ml-4`

## 7. Column heading

`mb-2 text-xs font-semibold uppercase tracking-wider text-gray-400`

## 8. Link item

```
flex items-start gap-3 w-full rounded-lg px-3 py-2.5
hover:bg-gray-50 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
group transition-colors
```

Link label: `text-sm font-medium text-gray-700 group-hover:text-gray-900`

Link description: `mt-0.5 text-xs text-gray-500`

Link icon: `mt-0.5 h-5 w-5 shrink-0 text-gray-400 group-hover:text-blue-600`

## 9. Badge chip (on link)

`rounded-full bg-blue-100 px-1.5 py-0.5 text-xs font-semibold text-blue-700`

## 10. Footer

`border-t border-gray-100 bg-gray-50 px-4 py-3`
