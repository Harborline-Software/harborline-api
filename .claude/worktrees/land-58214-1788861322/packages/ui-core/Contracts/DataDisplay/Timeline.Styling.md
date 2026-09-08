# Timeline — Styling Contract

- **Component:** Timeline
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Timeline.Semantic.md) · [Interaction](./Timeline.Interaction.md) · [Accessibility](./Timeline.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Timeline.tsx`
- **Catalog row:** #136 Timeline (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Vertical orientation (default)

`<ol>`: `flex flex-col` + `className` passthrough.

`<li>`: `relative flex gap-4` + (when `alternating`): `justify-center`

---

## 2. Horizontal orientation

`<ol>`: `flex items-start overflow-x-auto` + `className` passthrough.

`<li>`: `flex flex-1 flex-col items-center min-w-[80px]`

Dot row: `flex w-full items-center`

Connector: `h-0.5 flex-1 bg-gray-200` (after non-last items)

Content: `mt-2 px-1 text-center`

---

## 3. Dot

Vertical: `flex h-6 w-6 shrink-0 items-center justify-center rounded-full border-2 bg-white` + color class

Horizontal: `h-3 w-3 shrink-0 rounded-full border-2` + color class

Icon inside dot: `flex h-3 w-3 items-center justify-center text-white`

---

## 4. Color classes

| color | Class |
|---|---|
| `default` | `bg-blue-600 border-blue-600` |
| `success` | `bg-green-600 border-green-600` |
| `warning` | `bg-amber-500 border-amber-500` |
| `error` | `bg-red-600 border-red-600` |
| `info` | `bg-sky-500 border-sky-500` |

> **M1 note:** All colors are hardcoded palette values — not design tokens.

---

## 5. Connector line (vertical)

`w-0.5 flex-1 bg-gray-200 my-1` (not rendered for last item)

---

## 6. Content text

Label: `text-sm font-medium text-gray-900`

Date: `text-xs text-gray-500`

Description: `mt-0.5 text-xs text-gray-400` (horizontal) or `text-xs text-gray-500` (vertical)
