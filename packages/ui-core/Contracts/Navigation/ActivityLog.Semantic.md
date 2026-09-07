# ActivityLog — Semantic Contract

- **Component:** ActivityLog
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **Companion contracts:** [Interaction](./ActivityLog.Interaction.md) · [Styling](./ActivityLog.Styling.md) · [Accessibility](./ActivityLog.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ActivityLog.tsx`

---

## 1. Purpose

TODO: describe what ActivityLog is and the primary use cases it serves.

## 2. Data model

```typescript
export interface ActivityLogProps {
  /** Visual variant. */
  variant?: 'default'
  children?: React.ReactNode
}
```

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'default'` | `'default'` | TODO |
| `children` | `ReactNode` | — | TODO |

## 4. Events

TODO: enumerate callbacks and their payloads (or state "none in v1").

## 5. Slots

TODO: named sub-components / slot props (or "none — single children slot").

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/navigation/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
