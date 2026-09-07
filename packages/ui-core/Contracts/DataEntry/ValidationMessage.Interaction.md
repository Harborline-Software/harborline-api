# ValidationMessage — Interaction Contract

- **Component:** ValidationMessage
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationMessage.Semantic.md) · [Accessibility](./ValidationMessage.Accessibility.md) · [Styling](./ValidationMessage.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationMessage.tsx`
- **Catalog row:** #145 ValidationMessage (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. No interaction

ValidationMessage is a pure display component. No event handlers, no state, no user interaction.

---

## 2. Mount / unmount behavior

When `message` transitions from falsy → truthy, the component mounts and `role="alert"` triggers an assertive AT announcement. When `message` becomes falsy, the component unmounts silently.

---

## 3. Known gaps

None.
