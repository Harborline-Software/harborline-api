# Wizard — Semantic Contract

- **Component:** Wizard
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Wizard.Interaction.md) · [Accessibility](./Wizard.Accessibility.md) · [Styling](./Wizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Wizard.tsx`
- **Catalog row:** #150 Wizard (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled multi-step wizard wrapper

---

## 1. Purpose

Wizard is a **multi-step form navigator** — a sequential step container with
a progress indicator, step content area, and Back/Next/Finish navigation
buttons. It manages step navigation internally.

Typical use: onboarding flows, multi-step creation forms (new property, new
lease, invite user), guided configurations.

Wizard manages its own `currentIndex` state (fully uncontrolled step navigation).
Hosts provide the step definitions and a completion callback.

---

## 2. Data model

```typescript
interface WizardStep {
  id: string
  title: string
  content: React.ReactNode
  canProceed?: boolean
}

interface WizardProps {
  steps: WizardStep[]
  onComplete: () => void
  completeLabel?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `steps` | `WizardStep[]` | _required_ | Ordered step definitions. Must contain ≥ 1 step. |
| `onComplete` | `() => void` | _required_ | Fires when the user clicks the final step's complete button. |
| `completeLabel` | `string` | `'Finish'` | Label for the final-step Next button. |
| `className` | `string` | — | Applied to the root container `div`. |

### 3.1 `WizardStep`

| Field | Type | Default | Meaning |
|---|---|---|---|
| `id` | `string` | _required_ | Unique step identifier. |
| `title` | `string` | _required_ | Step title displayed in the progress indicator and step label. |
| `content` | `ReactNode` | _required_ | Step body content rendered in the content area. |
| `canProceed` | `boolean` | `true` | When `false`, the Next/Finish button is disabled. Use to gate progression on validation. |

### 3.2 Fully uncontrolled step state

Wizard manages `currentIndex` internally — there is no `currentStep` or
`onStepChange` prop in M1. The host cannot programmatically navigate to a step.
This matches the "form wizard" use case where the user always navigates linearly.

### 3.3 No backwards step skipping

The step indicator is display-only — past steps are NOT clickable links to jump
back. Users navigate only via the Back and Next buttons.

### 3.4 canProceed gate

When `canProceed === false` on the current step:
- The Next/Finish button is `disabled`.
- The Back button remains enabled (always).

---

## 4. Events — semantics

| Event | Payload | Fired when |
|---|---|---|
| `onComplete` | — | User clicks the Finish button on the last step. |

Wizard does not expose `onStepChange` in M1.

---

## 5. Deferred features

- Controlled step navigation (`currentStep` prop + `onStepChange`).
- Clickable past-step indicators for non-linear navigation.
- Step validation summary (show all validation errors before allowing Next).
- Vertical (sidebar) step indicator layout.
- Skippable steps.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-WZ1 | High | No controlled step navigation — host cannot programmatically navigate steps | Accepted-risk M1; uncontrolled covers linear form wizards |
| G-WZ2 | Medium | No step-level error state in the progress indicator | Deferred |
