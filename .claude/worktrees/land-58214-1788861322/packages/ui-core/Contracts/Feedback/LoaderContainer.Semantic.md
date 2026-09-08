# LoaderContainer — Semantic Contract

- **Component:** LoaderContainer
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./LoaderContainer.Interaction.md) · [Accessibility](./LoaderContainer.Accessibility.md) · [Styling](./LoaderContainer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/LoaderContainer.tsx`
- **Catalog row:** #80 LoaderContainer (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled overlay container

---

## 1. Component purpose

**LoaderContainer** — a layout wrapper that overlays a centered loading spinner on its children when `loading=true`. Children remain mounted and visible beneath the overlay. Delegates spinner rendering to the `Loader` component.

---

## 2. Props

```typescript
interface LoaderContainerProps {
  loading: boolean                         // required; controls overlay visibility
  loaderProps?: LoaderProps                // optional; forwarded to inner Loader
  overlay?: boolean                        // default: true; adds backdrop blur/tint
  children?: React.ReactNode
  className?: string
}
```

---

## 3. Overlay modes

When `overlay=true` (default): a semi-transparent blurred backdrop covers the children, visually dimming them while the spinner is centered above.

When `overlay=false`: only the spinner is shown (absolutely positioned, centered) with no backdrop — children are fully visible behind.

---

## 4. Relationship to Loader

`LoaderContainer` is a layout primitive. It does not implement the spinner directly; it renders `<Loader {...loaderProps} />` at the center of the overlay. Any `loaderProps` are forwarded verbatim to `Loader`. See the Loader component contracts for spinner sizing, color, and appearance.

---

## 5. Children always mounted

Children are not conditionally unmounted when `loading=true`. They remain in the DOM, covered by the overlay. This is intentional: forms and tables stay mounted during save/fetch operations.
