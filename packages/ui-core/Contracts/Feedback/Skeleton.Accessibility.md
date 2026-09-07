# Skeleton — Accessibility Contract

- **Component:** Skeleton
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Skeleton.Semantic.md) · [Interaction](./Skeleton.Interaction.md) · [Styling](./Skeleton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Skeleton.tsx`
- **Catalog row:** #118 Skeleton (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-hidden="true"` | `<div>` | Completely hidden from AT |

Skeletons are decorative — they carry no content meaning. AT must not announce them.

---

## 2. Loading region

Callers are responsible for announcing loading state to AT if needed (e.g., `role="status"` or `aria-busy="true"` on the surrounding container).

---

## 3. Known gaps

None. Skeleton correctly hides itself from AT via `aria-hidden`.
