# MultiStepForm — Semantic Contract

- **Component:** MultiStepForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MultiStepForm.Interaction.md) · [Accessibility](./MultiStepForm.Accessibility.md) · [Styling](./MultiStepForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiStepForm.tsx`
- **Catalog row:** #A36 MultiStepForm (`app-priority: low`, `library-scope: planned`) — Harborline-native multi-step form; simpler than Wizard #147
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled wizard form wrapper

---

## 1. Component purpose

**MultiStepForm** — a stepped form progress indicator. Renders a step header nav showing each step as a numbered circle (or checkmark when complete) with a connecting line. The actual form content is passed as `children` and rendered below the step nav.

---

## 2. Props

```typescript
interface MultiStepFormStep {
  id: string
  title: string
  description?: string
}

interface MultiStepFormProps {
  steps: MultiStepFormStep[]        // required; step definitions
  currentStep: number               // required; 0-based index of active step
  onStepChange?: (step: number) => void
  allowNavigation?: boolean         // default: false; allows clicking completed steps
  children: React.ReactNode         // required; form content for current step
  className?: string
}
```

---

## 3. Step states

| Condition | State |
|---|---|
| `i < currentStep` | Complete — filled blue circle with checkmark SVG |
| `i === currentStep` | Current — outline circle with step number; `aria-current="step"` |
| `i > currentStep` | Future — gray outline circle with step number |

---

## 4. Step navigation

When `allowNavigation=false` (default), step circles are disabled buttons — the user cannot click to navigate. Only the parent controls step progression via `currentStep`.

When `allowNavigation=true`, completed steps (i < currentStep) become clickable and call `onStepChange(i)` on click. Current and future steps remain non-clickable.

---

## 5. Content rendering

`children` is rendered unchanged below the step nav inside a `<div>`. The component does not conditionally show/hide content by step — the parent is responsible for rendering the correct step's content.
