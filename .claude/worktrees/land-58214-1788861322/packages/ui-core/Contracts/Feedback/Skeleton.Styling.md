# Skeleton — Styling Contract

- **Component:** Skeleton
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Skeleton.Semantic.md) · [Interaction](./Skeleton.Interaction.md) · [Accessibility](./Skeleton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Skeleton.tsx`
- **Catalog row:** #118 Skeleton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Element

`animate-pulse rounded-md bg-gray-200` + `className` passthrough.

> **M1 note:** `bg-gray-200` is a hardcoded palette class. Replace with `bg-muted` in M2+.

No default size — callers must provide `width`/`height` via `className` (e.g., `h-4 w-32`).
