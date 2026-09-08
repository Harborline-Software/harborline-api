# MultiStepForm — Interaction Contract

- **Component:** MultiStepForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiStepForm.Semantic.md) · [Accessibility](./MultiStepForm.Accessibility.md) · [Styling](./MultiStepForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiStepForm.tsx`
- **Catalog row:** (not-in-catalog) MultiStepForm (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Step click behavior

```typescript
onClick={() => allowNavigation && isComplete(i) && onStepChange?.(i)}
```

A step button's click fires `onStepChange(i)` only when `allowNavigation=true` AND the step is complete (`i < currentStep`). Otherwise the click is silently ignored.

---

## 2. Disabled state logic

```
disabled = !allowNavigation || (!isComplete(i) && !isCurrent(i))
```

- `allowNavigation=false`: all steps disabled
- `allowNavigation=true`: only complete steps are enabled; current and future steps are disabled

---

## 3. Controlled-only

`MultiStepForm` is fully controlled — it has no internal `currentStep` state. The parent must increment/decrement `currentStep` in response to form actions (Next/Back buttons, validation, etc.). `MultiStepForm` only handles step-circle navigation clicks.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MSF1 | Low | No visual feedback on step click (e.g., no active/pressed state beyond native button behavior) | Accepted-risk M1 |
