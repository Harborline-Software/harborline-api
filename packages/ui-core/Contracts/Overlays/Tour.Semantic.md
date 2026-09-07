# Tour — Semantic Contract

- **Component:** Tour / GuidedWalkthrough
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Tour.Interaction.md) · [Accessibility](./Tour.Accessibility.md) · [Styling](./Tour.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Ant Design Tour / Shepherd.js baseline)
- **Catalog row:** #A7 Tour / GuidedWalkthrough (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Tour baseline)

---

## 1. Component purpose

**Tour** — a multi-step guided walkthrough overlay that highlights specific elements in the UI and renders a popover with instructional content. Used for onboarding flows, feature introductions, and contextual help sequences.

---

## 2. Props (planned)

```typescript
interface TourStep {
  target?: React.RefObject<HTMLElement> | (() => HTMLElement | null)
  title?: React.ReactNode
  description?: React.ReactNode
  placement?: 'top' | 'bottom' | 'left' | 'right' | 'center'  // default: 'bottom'
  prevButtonProps?: React.ButtonHTMLAttributes<HTMLButtonElement>
  nextButtonProps?: React.ButtonHTMLAttributes<HTMLButtonElement>
  cover?: React.ReactNode                // image or media above content
}

interface TourProps {
  open: boolean
  onClose: () => void
  steps: TourStep[]
  current?: number                       // controlled step index; default: 0
  defaultCurrent?: number                // uncontrolled initial step
  onChange?: (current: number) => void
  onFinish?: () => void                  // called when last step is dismissed
  mask?: boolean                         // darken background; default: true
  maskClosable?: boolean                 // clicking mask closes tour; default: true
  type?: 'default' | 'primary'           // popover color scheme; default: 'default'
  indicatorsRender?: (current: number, total: number) => React.ReactNode
  zIndex?: number                        // default: 1001
  className?: string
}
```

---

## 3. Step targeting

Each step's `target` points to a DOM element to highlight. The tour renders a spotlight (cutout in the mask) around the target's bounding rect. When `target` is undefined, the popover renders centered in the viewport (no spotlight).

---

## 4. Controlled vs uncontrolled

When `current` is supplied, step navigation is controlled by the caller via `onChange`. When omitted, internal state manages the step index. `onFinish` fires after the last step's Next action.

---

## 5. Mask

When `mask=true` (default), an overlay darkens the page except for the target highlight. When `mask=false`, the popover renders without any dimming.
