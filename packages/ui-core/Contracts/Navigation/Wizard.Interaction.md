# Wizard — Interaction Contract

- **Component:** Wizard
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Wizard.Semantic.md) · [Accessibility](./Wizard.Accessibility.md) · [Styling](./Wizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Wizard.tsx`
- **Catalog row:** #150 Wizard (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
currentIndex = 0

STEP_N (0..steps.length-1)
  → Next click (canProceed=true, not last) → currentIndex++
  → Back click (not first) → currentIndex--
  → Next click (canProceed=true, last step) → onComplete()
  → Next click (canProceed=false) → no-op (button disabled)
  → Back click (first step) → no-op (button disabled)
```

`currentIndex` is managed by `React.useState` — fully internal, no host control.

---

## 2. Next button behaviour

| Condition | Label | Enabled |
|---|---|---|
| Not last step, `canProceed=true` | `"Next"` | Yes |
| Not last step, `canProceed=false` | `"Next"` | No (disabled) |
| Last step, `canProceed=true` | `completeLabel` ("Finish") | Yes |
| Last step, `canProceed=false` | `completeLabel` | No (disabled) |

Clicking Next on the last enabled step calls `onComplete()` — the host is
responsible for form submission, navigation, or modal close.

---

## 3. Back button behaviour

| Condition | Enabled |
|---|---|
| First step (`currentIndex === 0`) | No (disabled) |
| Any later step | Yes |

Back always decrements `currentIndex` — no `canProceed` check on Back.

---

## 4. Step progress indicator

The step indicators are display-only:
- Past steps (`i < currentIndex`): completed state (filled circle with checkmark).
- Active step (`i === currentIndex`): active state (ring outline).
- Future steps (`i > currentIndex`): pending state (gray background).

The step indicators are NOT clickable for navigation in M1.

---

## 5. No keyboard shortcuts

Wizard adds no custom keyboard shortcuts. Navigation is via the Back/Next buttons
only. Tab order: Back button → Next button (in the footer). Step content is
naturally in the tab flow above the buttons.

---

## 6. Content mounting

Only the current step's `content` is rendered (`steps[currentIndex].content`).
Previous and future step content is unmounted — there is no `forceMount` option
in M1. Hosts that need to preserve form state across steps must lift state out
of the step content.
