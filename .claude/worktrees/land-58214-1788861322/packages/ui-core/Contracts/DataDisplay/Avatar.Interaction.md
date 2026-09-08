# Avatar — Interaction Contract

- **Component:** Avatar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Avatar.Semantic.md) · [Accessibility](./Avatar.Accessibility.md) · [Styling](./Avatar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Avatar.tsx`
- **Catalog row:** #8 Avatar (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
SHOWING_IMAGE (imgError=false, src present)
  → img onError → SHOWING_FALLBACK

SHOWING_FALLBACK (imgError=true or no src)
  → (no transitions; shows initials/icon/silhouette)
```

---

## 2. Image error handling

When the `<img>` fires `onError`, `setImgError(true)` — the image is hidden and the fallback renders. This is one-way; the error state cannot be reset without remounting.

---

## 3. No user interactions

Avatar has no click handler, no selection state, and no hover state by default. To make an avatar interactive, wrap it in a `<button>` or attach an `onClick` handler via `className`-based host styling.
