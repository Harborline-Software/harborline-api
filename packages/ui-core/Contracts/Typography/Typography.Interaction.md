# Typography — Interaction Contract

- **Component:** Typography
- **ADR 0017 family:** Typography
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** #143 Typography (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

Typography is stateless with no user interactions.

```
DISPLAY_ONLY
  → (no transitions)
```

Typography renders text content. All interactive behaviour belongs to parent context. Typography components are not focusable unless `as="a"` or similar interactive element is used.
