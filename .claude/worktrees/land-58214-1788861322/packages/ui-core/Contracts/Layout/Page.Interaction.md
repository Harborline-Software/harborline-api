# Page — Interaction Contract

- **Component:** Page
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Page.Semantic.md) · [Accessibility](./Page.Accessibility.md) · [Styling](./Page.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Page.tsx`
- **Catalog row:** Page (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Interaction model

Page is a layout shell with no intrinsic interactive behavior of its own. The only "interaction" is
native scrolling of the body `<section>` (via `overflow-auto`) — no JS scroll handling, no scroll
restoration, no virtualization. Interactive content lives in the header `actions` slot and the body
`children`, each owning its own interaction contract.

---

## 2. Scroll independence

The header (optionally `sticky`) stays fixed while the body scrolls. This is pure CSS; Page adds no
scroll listeners.

---

## 3. Known gaps

- No built-in scroll-restoration or "scroll to top on route change" — callers manage that at the
  router layer if desired.
