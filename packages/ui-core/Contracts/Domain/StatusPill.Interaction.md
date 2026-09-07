# StatusPill — Interaction Contract

- **Component:** StatusPill
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StatusPill.Semantic.md) · [Accessibility](./StatusPill.Accessibility.md) · [Styling](./StatusPill.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/StatusPill.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

StatusPill is purely informational. No clickable elements, no events, no
state transitions. It renders a single `<span>` with status text and optional
browser tooltip.

---

## 2. Known gaps

None. Component is intentionally display-only.
