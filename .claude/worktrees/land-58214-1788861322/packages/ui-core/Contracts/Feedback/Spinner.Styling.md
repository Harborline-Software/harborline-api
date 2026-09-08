# Spinner — Styling Contract

- **Component:** Spinner
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Spinner.Semantic.md) · [Interaction](./Spinner.Interaction.md) · [Accessibility](./Spinner.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Spinner.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. SVG element

`animate-spin text-current` + size class + `className` passthrough.

The spinner inherits color from `currentColor` — it takes the text color of its container.

---

## 2. Size classes

| `size` | Classes |
|---|---|
| `xs` | `h-3 w-3` |
| `sm` | `h-4 w-4` |
| `md` (default) | `h-5 w-5` |
| `lg` | `h-6 w-6` |

---

## 3. SVG internals

- Circle: `opacity-25` (track ring)
- Path: `opacity-75` (spinning arc)
