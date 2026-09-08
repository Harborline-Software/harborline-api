# BadgeContainer — Interaction Contract

- **Component:** BadgeContainer
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Draft
- **Companion contracts:** [Semantic](./BadgeContainer.Semantic.md) · [Styling](./BadgeContainer.Styling.md) · [Accessibility](./BadgeContainer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/BadgeContainer.tsx`

---

## 1. Scope

`BadgeContainer` governs **no** interactive behavior of its own. It is a static
positioning wrapper: the absence of behavior is itself the contract. Any
interactivity belongs to (a) the wrapped element passed as `children`
(e.g. the icon button or avatar) and (b) the overlay `Badge`. This contract
exists to assert those boundaries so consumers do not expect the wrapper to
intercept focus, pointer, or keyboard events.

## 2. Activation & state

Stateless. There is no controlled/uncontrolled lifecycle, no internal state, and
no callbacks. The component re-renders only when its `children`/props change.
The wrapper does not become a focus target and does not alter the tab order of
its descendants.

## 3. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab | Passes through — the wrapper is not focusable; focus lands on focusable descendants (the wrapped control) in DOM order. |
| Enter / Space | Not handled — delegated to the wrapped control / `Badge`. |

## 4. Disabled handling

No disabled concept. Disabled state belongs to the wrapped interactive element;
`BadgeContainer` neither exposes nor propagates a `disabled` prop.

## 5. Interaction-state precedence

Not applicable — the wrapper has no visual interaction states (no hover / focus /
active / disabled treatments of its own). Interaction-state precedence is
defined by the wrapped control and by the overlay `Badge`, not by this wrapper.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/badges/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
