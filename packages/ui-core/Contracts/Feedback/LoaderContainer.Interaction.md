# LoaderContainer — Interaction Contract

- **Component:** LoaderContainer
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoaderContainer.Semantic.md) · [Accessibility](./LoaderContainer.Accessibility.md) · [Styling](./LoaderContainer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/LoaderContainer.tsx`
- **Catalog row:** #80 LoaderContainer (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
IDLE (loading=false)
  → children render normally; no overlay; no spinner

LOADING (loading=true)
  → overlay appears above children
  → spinner centered in overlay
  → children remain mounted (pointer-events blocked by overlay)
  → loading=false → IDLE
```

---

## 2. User interaction during loading

When `loading=true`, the overlay (`pointer-events: auto` on the absolute cover) prevents all pointer events from reaching the children beneath. Children remain visible but are non-interactive. This is not explicitly enforced by adding `pointer-events-none` to children — the overlay intercepts events by covering them.

---

## 3. No user-triggered transitions

`LoaderContainer` has no user-triggered interaction. The `loading` prop is the sole state driver; the parent controls when loading starts and ends. There are no click, hover, or keyboard interactions on the container itself.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LC1 | Low | Children behind overlay may still receive keyboard focus (Tab key bypasses pointer-events) — keyboard users can interact with obscured form fields | Accepted-risk M1; most use cases are async data fetches where keyboard interaction during load is low-risk |
