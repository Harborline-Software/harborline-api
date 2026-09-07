# NumberBadge — Interaction Contract

- **Component:** NumberBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberBadge.Semantic.md) · [Accessibility](./NumberBadge.Accessibility.md) · [Styling](./NumberBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NumberBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

NumberBadge is purely informational. No clickable elements, no events.
It is typically composed inside a button or link whose click handler
is owned by the host.

---

## 2. Known gaps

None. Component is intentionally display-only.
