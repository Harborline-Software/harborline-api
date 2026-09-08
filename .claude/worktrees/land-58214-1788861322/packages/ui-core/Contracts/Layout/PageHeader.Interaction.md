# PageHeader — Interaction Contract

- **Component:** PageHeader
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PageHeader.Semantic.md) · [Accessibility](./PageHeader.Accessibility.md) · [Styling](./PageHeader.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/PageHeader.tsx`
- **Catalog row:** PageHeader (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Interaction model

PageHeader is a layout container with no intrinsic interactive behavior, event handlers, or
keyboard model of its own. Any interactivity comes from children passed into the `actions` slot
(or `children`) — e.g. Button, Badge, Chip — which own their own interaction contracts.

---

## 2. Sticky behavior

`sticky` is a pure CSS positioning concern (`position: sticky`); it introduces no JS, no scroll
listeners, and no focus management.

---

## 3. Known gaps

None identified.
