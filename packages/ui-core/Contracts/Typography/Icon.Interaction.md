# Icon — Interaction Contract

- **Component:** Icon
- **ADR 0017 family:** Typography
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** #70 Icon (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

Icon and SVGIcon are stateless display components with no user interactions.

```
DISPLAY_ONLY
  → (no transitions)
```

Icons are not clickable, not focusable, and have no hover states by default. To make an icon interactive, wrap it in a `<button>` or `<a>`.
