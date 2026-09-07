# SyncStateBadge — Interaction Contract

- **Component:** SyncStateBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SyncStateBadge.Semantic.md) · [Accessibility](./SyncStateBadge.Accessibility.md) · [Styling](./SyncStateBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/SyncStateBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

SyncStateBadge is purely informational. No clickable elements, no events,
no state transitions within the component. The `state` prop is fully
host-controlled.

---

## 2. Known gaps

None. Component is intentionally display-only.
