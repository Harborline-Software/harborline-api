# CommandPalette — Styling Contract

- **Component:** CommandPalette
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CommandPalette.Semantic.md) · [Interaction](./CommandPalette.Interaction.md) · [Accessibility](./CommandPalette.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/CommandPalette.tsx`
- **Catalog row:** #A1 CommandPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Viewport overlay

`fixed inset-0 z-50 flex items-start justify-center pt-[20vh] p-4`

---

## 2. Backdrop

`fixed inset-0 bg-black/40`

---

## 3. Dialog panel

`relative z-10 w-full max-w-lg overflow-hidden rounded-xl bg-white shadow-2xl ring-1 ring-black/10`

> **M1 note:** Uses hardcoded `bg-white` rather than `bg-background` design token.

---

## 4. Search bar

`flex items-center gap-3 border-b border-gray-200 px-4`

Search icon: `h-4 w-4 shrink-0 text-gray-400`

Input: `flex-1 py-4 text-sm text-gray-900 placeholder:text-gray-400 focus:outline-none`

Kbd hint: `hidden rounded border border-gray-200 bg-gray-50 px-1.5 py-0.5 text-[10px] text-gray-500 sm:block`

---

## 5. Results list

`max-h-72 overflow-y-auto py-2`

No-results state: `px-4 py-8 text-center text-sm text-gray-500`

---

## 6. Group header

`px-4 pb-1 pt-3 text-[11px] font-semibold uppercase tracking-wider text-gray-400`

---

## 7. Item — base

`mx-2 flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 text-sm`

| State | Classes |
|---|---|
| Active (keyboard) | `bg-blue-600 text-white` |
| Inactive | `text-gray-700 hover:bg-gray-100` |

Icon (active): `text-blue-200`
Icon (inactive): `text-gray-400`

Description (active): `truncate text-xs text-blue-200`
Description (inactive): `truncate text-xs text-gray-400`

> **M1 note:** Uses hardcoded gray/blue Tailwind values rather than design system tokens.
