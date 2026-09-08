# Stepper — Semantic Contract

- **Component:** Stepper
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** Interaction (PAO) · [Styling](./Stepper.Styling.md) · [Accessibility](./Stepper.Accessibility.md)
- **Related contracts:** [TabStrip.Semantic.md](../Navigation/TabStrip.Semantic.md) — alternative for non-sequential panel switching; [Accordion.Semantic.md](./Accordion.Semantic.md) — collapsible sections without sequential semantics.
- **Reference implementation:** `packages/ui-react/src/components/layout/Stepper.tsx`
- **Catalog rows:** #160 Stepper / #48 Stepper (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)
- **Foundation:** none — hand-rolled multi-step wizard (no Radix primitive)

---

## 1. Purpose

Stepper is a **progress-indicator for sequential multi-step flows**. It renders
a horizontal or vertical list of numbered steps, each with a status
(pending / active / complete / error), label, and optional description.

Stepper is **purely presentational** — it does not control which step is shown
or handle next/back navigation. The host manages wizard state and passes
`activeStep` to reflect the current position.

**Primary Harborline use cases:**

- Onboarding wizard (Add property → Add units → Add tenants → Review)
- Import wizard (Upload → Map fields → Validate → Confirm)
- Approval workflow visualization (Submitted → Under review → Approved)

**When to use Stepper vs alternatives:**
- **Stepper** (this) — the single canonical step-progress indicator. Supports 4 statuses
  (pending / active / complete / error), both index-based (`currentStep`) and value-based
  (`activeStep`) derivation, horizontal and vertical orientations.
- **Wizard** (#150) — when you need Back/Next/Finish navigation buttons and `canProceed`
  gating. Wizard is a flow controller; Stepper is a display-only indicator.
- **TabStrip** — for non-sequential panel switching (any tab can be activated in any order).

> **Consolidation note (2026-06-12):** StepList and Steps were separate components with
> near-identical rendering. They have been consolidated into Stepper per CIC ruling KS-6 Q3.
> See `Navigation/StepList.Semantic.md` and `Navigation/Steps.Semantic.md` for redirect stubs.

---

## 2. Data model

```typescript
type StepStatus = 'pending' | 'active' | 'complete' | 'error'

interface StepperStep {
  value: string           // unique identifier
  label: string           // short label below/beside the step indicator
  description?: string    // secondary line (e.g. "3 errors found")
  status?: StepStatus     // host-supplied override; if absent, derived from activeStep/currentStep
}

interface StepperProps {
  steps: StepperStep[]
  activeStep?: string
  /**
   * Index-based active step (0-based convenience prop; mirrors the StepList API).
   * Ignored when `activeStep` is also provided.
   */
  currentStep?: number
  orientation?: 'horizontal' | 'vertical'
}
```

> **Consolidation note (2026-06-12, CIC ruling KS-6 Q3):** `StepStatus` was formerly
> spelled `'complete'` in the original Stepper release. Renamed to `'complete'` to unify
> with the StepList canonical vocabulary when StepList and Steps were consolidated into
> Stepper. `'complete'` is no longer a valid value.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `steps` | `StepperStep[]` | _required_ | The ordered list of steps. |
| `activeStep` | `string` | — | The `value` of the currently active step. Steps before it are `complete`; steps after it are `pending`. When absent, all steps are `pending`. |
| `currentStep` | `number` | — | 0-based index of the active step. Convenience prop for callers that track a step index rather than a step value. Ignored when `activeStep` is also provided. |
| `orientation` | `'horizontal' \| 'vertical'` | `'horizontal'` | `'horizontal'` renders steps in a row with connecting lines. `'vertical'` stacks steps with a connecting line to the left. |
| HTML attributes | — | — | Spread onto the root `<ol>`. |

### 3.1 Status derivation

If `StepperStep.status` is NOT supplied, the component derives status from
the step's position relative to `activeStep`:

| Position | Derived status |
| --- | --- |
| Before `activeStep` | `complete` |
| Equals `activeStep` | `active` |
| After `activeStep` | `pending` |

If `StepperStep.status` IS supplied, it overrides the derived status for that
step. This allows:

- Marking a step `error` without changing `activeStep`
- Marking a future step `complete` (e.g. skipped or pre-fulfilled)
- Marking a past step `pending` (e.g. step that needs re-visit)

### 3.2 Status semantics

| Status | Indicator | Semantic |
| --- | --- | --- |
| `pending` | Number (gray) | Not yet reached |
| `active` | Number (blue outline) | Currently in progress |
| `complete` | Check mark (blue fill) | Successfully complete |
| `error` | ✕ mark (red fill) | Failed; requires attention |

### 3.3 Horizontal orientation

Steps are arranged in a row. Each step node has a connector line to the next.
The connector is blue for complete segments, gray for pending.

### 3.4 Vertical orientation

Steps are stacked vertically. Each step node has a vertical connector line
below it. Useful for narrower layouts and timeline-style displays.

### 3.5 ARIA

Stepper renders a native `<ol>` (implicit `role="list"`) as the container and
native `<li>` (implicit `role="listitem"`) for each step. The active step
carries `aria-current="step"`. There is no dedicated `stepper` ARIA role —
this list pattern is the WAI-ARIA-recommended approach for progress steps.

**Implementation note:** use `<ol>` + `<li>` elements, NOT `<div role="list">` /
`<div role="listitem">`. VoiceOver on Safari drops `role="list"` semantics when
`list-style: none` is applied to non-list elements, silently breaking AT navigation.

---

## 4. Events — semantics

Stepper has **no events**. It is purely presentational. Navigation
(next/back/jump) belongs to the host wizard or workflow controller.

---

## 5. Slots

Stepper accepts no slots beyond the structured `steps` array. Rich per-step
content (icons, badges, custom indicators) is deferred (§7).

---

## 6. Component composition

### Horizontal onboarding wizard

```tsx
const STEPS = [
  { value: 'property', label: 'Add property' },
  { value: 'units',    label: 'Add units' },
  { value: 'tenants',  label: 'Add tenants' },
  { value: 'review',   label: 'Review' },
]

<Stepper steps={STEPS} activeStep={currentStep} />
// ... step content ...
<div className="flex gap-2">
  <Button onClick={goBack}>Back</Button>
  <Button variant="primary" onClick={goNext}>Next</Button>
</div>
```

### Vertical import wizard with error

```tsx
<Stepper
  orientation="vertical"
  steps={[
    { value: 'upload',   label: 'Upload',      status: 'complete' },
    { value: 'map',      label: 'Map fields',  status: 'complete' },
    { value: 'validate', label: 'Validate',    status: 'error', description: '3 errors found' },
    { value: 'confirm',  label: 'Confirm',     status: 'pending' },
  ]}
/>
```

### Read-only approval workflow

```tsx
<Stepper
  steps={approvalSteps.map(s => ({
    value: s.id,
    label: s.name,
    description: s.completeAt ? formatDate(s.completeAt) : undefined,
    status: s.status,
  }))}
/>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Clickable steps** — allow jumping to a complete step by clicking its
  indicator. Requires an `onStepClick?: (value: string) => void` prop.
- **Custom step icons** — replace the number/check/× indicator with a
  custom icon per step (`icon?: ReactNode`).
- **`size` variants** — compact (small indicators) vs default size.
- **Non-linear stepper** — steps that can be visited in any order (some
  design systems have "non-linear" mode). Deferred; use `status` override
  for today.
- **Mobile responsive** — collapse horizontal Stepper to a "Step N of M"
  label on narrow viewports.

---

## 8. Open questions (for council)

1. **Clickable steps in M2.** Should `onStepClick` be added in M2? Leaning
   yes — going back to a previous step is needed in most real wizard UIs.
2. **`activeStep` vs `activeIndex`.** `activeStep` uses the `value` string.
   An alternative is `activeIndex: number` (simpler for linear flows).
   Leaning `activeStep` — consistent with TabStrip and SelectField APIs.
3. **`description` line.** Should `description` be `ReactNode` or `string`?
   Leaning `string` — descriptions are short error messages or dates; rich
   content is rare.
4. **Vertical connector line height.** In vertical mode, the connector line's
   height is determined by the content below the step label. This is natural
   but may be inconsistent across steps with different description lengths.
   Leaning accept the natural height — no fixed connector length.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/layout/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
