# EmptyState — Styling Contract

- **Component:** EmptyState
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Interaction](./EmptyState.Interaction.md) · [Accessibility](./EmptyState.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex flex-col items-center py-12 px-6 text-center` — vertically stacked,
center-aligned, generous vertical padding for visual breathing room.

---

## 2. Icons

`h-8 w-8` icons from `lucide-react`, `aria-hidden="true"`.

| Variant | Icon | Color |
| --- | --- | --- |
| `informational` | `<Info>` | `text-gray-300` |
| `positive` | `<CheckCircle2>` | `text-green-300` |
| `actionable` | `<PlusCircle>` | `text-gray-300` |

---

## 3. Typography

| Element | Classes |
| --- | --- |
| Title `<p>` | `mt-3 text-base font-medium text-gray-700` |
| Description `<p>` | `mt-1 text-sm text-gray-500` |

---

## 4. Action button

`mt-4 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2`

Neutral ghost-style button consistent with the informational tone.
