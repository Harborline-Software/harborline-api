# LoadingState — Styling Contract

- **Component:** LoadingState
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoadingState.Semantic.md) · [Interaction](./LoadingState.Interaction.md) · [Accessibility](./LoadingState.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/LoadingState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Variants

| Variant | Element | Classes |
| --- | --- | --- |
| `page` (default) | `<div>` | `flex items-center justify-center h-48 text-gray-500` |
| `inline` | `<p>` | `text-sm text-gray-500` |

---

## 2. Notes

LoadingState has no background color, border, or icon. It is intentionally
minimal — relying entirely on the label text for communication. No animation.
