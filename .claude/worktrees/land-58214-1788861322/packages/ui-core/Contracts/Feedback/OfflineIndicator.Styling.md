# OfflineIndicator — Styling Contract

- **Component:** OfflineIndicator
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OfflineIndicator.Semantic.md) · [Interaction](./OfflineIndicator.Interaction.md) · [Accessibility](./OfflineIndicator.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/OfflineIndicator.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex items-center gap-2 rounded-md bg-warning/10 border border-warning/20 px-4 py-2 text-sm text-warning` + `className` passthrough.

Uses `text-warning`, `bg-warning/10`, and `border-warning/20` design tokens
(not hardcoded palette classes). These must be present in the consuming app's
Tailwind theme config.

---

## 2. Icon

`h-4 w-4 shrink-0` SVG warning triangle, `aria-hidden="true"`, inherits
`text-warning` from container.
