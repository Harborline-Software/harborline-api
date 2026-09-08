# Alert — Styling Contract

- **Component:** Alert
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Alert.Semantic.md) · [Interaction](./Alert.Interaction.md) · [Accessibility](./Alert.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Alert.tsx`
- **Catalog rows:** #153 Alert / #78 CalloutBox (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Container

Base: `rounded-md border p-4` + variant class + `className` passthrough.

> **M1 note:** All variant colors are hardcoded Tailwind palette classes, not design tokens.

---

## 2. Variant classes

| Variant | Container | Icon | Title |
|---|---|---|---|
| `info` | `border-blue-200 bg-blue-50 text-blue-800` | `text-blue-600` | `text-blue-900` |
| `success` | `border-green-200 bg-green-50 text-green-800` | `text-green-600` | `text-green-900` |
| `warning` | `border-amber-200 bg-amber-50 text-amber-900` | `text-amber-600` | `text-amber-900` |
| `error` | `border-red-200 bg-red-50 text-red-800` | `text-red-600` | `text-red-900` |

---

## 3. Layout

`flex gap-3` — icon + content side by side.

Content area: `flex-1 min-w-0`

Title: `mb-1 text-sm font-semibold` + variant title class

Body text: `text-sm`

Action slot: `mt-3`

Icon: `h-5 w-5 shrink-0 mt-0.5`

---

## 4. Dismiss button

Base: `-mt-1 -mr-1 shrink-0 rounded p-1 transition-colors focus:outline-none focus:ring-2 focus:ring-current focus:ring-offset-1`

| Variant | Hover class |
|---|---|
| `info` | `hover:bg-blue-200/60` |
| `success` | `hover:bg-green-200/60` |
| `warning` | `hover:bg-amber-200/60` |
| `error` | `hover:bg-red-200/60` |

Icon: `h-4 w-4` (X glyph, `aria-hidden`)
