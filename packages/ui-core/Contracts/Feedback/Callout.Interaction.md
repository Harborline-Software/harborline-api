# Callout — Interaction Contract

- **Component:** Callout
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Callout.Semantic.md) · [Accessibility](./Callout.Accessibility.md) · [Styling](./Callout.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Callout.tsx`
- **Catalog row:** (not-in-catalog) Callout (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Interaction model

Callout is a **read-only display component**. No interactive elements, no events.
