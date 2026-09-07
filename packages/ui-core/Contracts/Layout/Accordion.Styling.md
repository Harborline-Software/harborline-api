# Accordion — Styling Contract

- **Component:** Accordion
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Accordion.Semantic.md) · [Interaction](./Accordion.Interaction.md) · [Accessibility](./Accordion.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Accordion.tsx`
- **Catalog rows:** #155 Accordion / #35 PanelBar (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. Container

`divide-y divide-gray-200 rounded-md border border-gray-200` + `className` passthrough.

> **M1 note:** Border and divider colors are hardcoded palette classes, not design tokens.

---

## 2. Header button

Base: `flex w-full items-center justify-between px-4 py-3 text-sm font-medium text-gray-900 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-inset focus:ring-blue-500 transition-colors`

Disabled: `cursor-not-allowed opacity-50`

Title span: `text-left`

---

## 3. Chevron icon

`h-4 w-4 shrink-0 text-gray-400 transition-transform duration-200` + `rotate-180` when open.

---

## 4. Panel content wrapper

`px-4 pb-4 pt-2 text-sm text-gray-700`

The panel `<div>` uses `hidden` attribute for visibility — no Tailwind visibility class.
