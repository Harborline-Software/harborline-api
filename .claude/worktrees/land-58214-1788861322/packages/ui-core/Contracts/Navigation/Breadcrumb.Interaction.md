# Breadcrumb — Interaction Contract

- **Component:** Breadcrumb
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Breadcrumb.Semantic.md) · [Accessibility](./Breadcrumb.Accessibility.md) · [Styling](./Breadcrumb.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Breadcrumb.tsx`
- **Catalog row:** #14 Breadcrumb (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

Breadcrumb is a **navigation landmark** with link-based interaction only. Non-current items with `href` are native `<a>` elements — browser handles navigation.

The component fires no events (`onPageChange`, callbacks, etc.). All navigation state is managed by the caller's routing layer.

---

## 2. Current item

Current items render as `<span>`, not `<a>` — clicking them has no effect.

---

## 3. Known gaps

None. Breadcrumb interaction is intentionally minimal.
