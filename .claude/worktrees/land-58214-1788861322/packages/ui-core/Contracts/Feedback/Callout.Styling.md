# Callout — Styling Contract

- **Component:** Callout
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Callout.Semantic.md) · [Interaction](./Callout.Interaction.md) · [Accessibility](./Callout.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Callout.tsx`
- **Catalog row:** (not-in-catalog) Callout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`rounded-md border p-4` + variant class + `className` passthrough.

> **M1 note:** All variant colors are hardcoded Tailwind palette classes, not design tokens.

---

## 2. Variant classes

| Variant | Container | Icon | Title |
|---|---|---|---|
| `info` (default) | `border-blue-200 bg-blue-50 text-blue-900` | `text-blue-500` | `text-blue-800` |
| `tip` | `border-green-200 bg-green-50 text-green-900` | `text-green-500` | `text-green-800` |
| `warning` | `border-amber-200 bg-amber-50 text-amber-900` | `text-amber-500` | `text-amber-800` |
| `error` | `border-red-200 bg-red-50 text-red-900` | `text-red-500` | `text-red-800` |

---

## 3. Layout

`flex gap-3` — icon + content side by side.

Icon `<span>`: `mt-0.5 shrink-0` + icon class.

Content `<div>`: `min-w-0 flex-1 text-sm`

Title `<p>`: `font-semibold` + title class.

Body: `mt-1` when title present; no offset when title absent.
