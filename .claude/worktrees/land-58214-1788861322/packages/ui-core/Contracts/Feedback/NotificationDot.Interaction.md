# NotificationDot — Interaction Contract

- **Component:** NotificationDot
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationDot.Semantic.md) · [Accessibility](./NotificationDot.Accessibility.md) · [Styling](./NotificationDot.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/NotificationDot.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

NotificationDot is a pure wrapper/overlay. All interaction is provided by
the `children` element (e.g., an icon button that, when clicked, opens a
panel). NotificationDot itself fires no events and has no clickable surface.

---

## 2. Known gaps

None. Component is intentionally display-only.
