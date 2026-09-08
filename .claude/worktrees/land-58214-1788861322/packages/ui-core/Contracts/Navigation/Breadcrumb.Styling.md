# Breadcrumb — Styling Contract

- **Component:** Breadcrumb
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Breadcrumb.Semantic.md) · [Interaction](./Breadcrumb.Interaction.md) · [Accessibility](./Breadcrumb.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Breadcrumb.tsx`
- **Catalog row:** #14 Breadcrumb (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Nav container

`flex` + `className` passthrough.

---

## 2. Item list

`flex flex-wrap items-center gap-1.5`

---

## 3. List item

`flex items-center gap-1.5`

---

## 4. Separator

Default: chevron SVG `h-4 w-4 shrink-0 text-gray-400` (`aria-hidden="true"`).

Custom separator: caller-provided `ReactNode` wrapped in `<span aria-hidden="true">`.

---

## 5. Item text

| State | Classes |
|---|---|
| Link (non-current) | `text-sm text-gray-500 hover:text-gray-700 hover:underline` |
| Current item | `text-sm font-medium text-gray-900` |
| Non-link, non-current | `text-sm text-gray-500` |

> **M1 note:** Colors are hardcoded palette classes, not design tokens.
