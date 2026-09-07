# Separator — Interaction Contract

- **Component:** Separator
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Separator.Semantic.md) · [Accessibility](./Separator.Accessibility.md) · [Styling](./Separator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Separator.tsx`
- **Catalog row:** #A13 Separator (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

Separator is stateless with no user interactions.

```
DISPLAY_ONLY
  → (no transitions)
```

Separator is not focusable, not clickable, and has no hover states.
