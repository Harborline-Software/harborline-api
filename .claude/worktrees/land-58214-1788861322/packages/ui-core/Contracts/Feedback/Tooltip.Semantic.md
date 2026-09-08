# Tooltip — Semantic Contract

- **Component:** Tooltip
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Tooltip.Interaction.md) · [Accessibility](./Tooltip.Accessibility.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled positioned `<div>` (no Radix Tooltip primitive)

---

## 1. Component purpose

Wraps a trigger element and shows a text tooltip on hover or focus. Custom implementation (not Radix) — a controlled `<span>` wrapper with pointer and focus event handlers.

---

## 2. Props

```typescript
type TooltipSide = 'top' | 'right' | 'bottom' | 'left'

interface TooltipProps {
  content: string           // tooltip text; plain string only
  side?: TooltipSide        // default: 'top'
  delayDuration?: number    // ms before showing on hover; default: 700
  skipDelayDuration?: number // accepted but not implemented in M1
  children: ReactNode       // the trigger element
  className?: string        // applied to the tooltip bubble
}
```

---

## 3. Data model

`content` is a plain string. Rich content (React nodes) is not supported in M1.

---

## 4. Trigger wrapping

Tooltip renders a `<span className="relative inline-flex">` wrapper around `children`. This changes the display context of the trigger from block to inline-flex. Hosts should be aware of this if the trigger is a block-level element.

---

## 5. Trigger binding — aria-describedby injection

For AT to announce the tooltip text, `aria-describedby` pointing to the tooltip element's `id` must be present on the **trigger element itself**, not on the wrapper span.

**Required pattern — `React.cloneElement`:**

```typescript
// Tooltip generates a stable id for the tooltip bubble:
const tooltipId = useId()

// Inject aria-describedby onto the child trigger:
const trigger = React.cloneElement(
  React.Children.only(children) as React.ReactElement,
  { 'aria-describedby': isVisible ? tooltipId : undefined }
)

// Tooltip bubble:
<span id={tooltipId} role="tooltip" ...>{content}</span>
```

This requires `children` to be a **single React element** with a DOM root that accepts `aria-describedby` (e.g., `<button>`, `<a>`, `<input>`). If `children` is a component that does not forward unknown props, the caller must add `aria-describedby` forwarding or wrap the trigger in a DOM element.

**Alternative — Radix `Slot`:** Use `<Slot aria-describedby={isVisible ? tooltipId : undefined}>` as the trigger wrapper. `Slot` merges its props onto the single child without adding a DOM node. Preferred for library-quality implementations.

**Do NOT** rely solely on the wrapper `<span>` receiving `aria-describedby` — screen readers associate `aria-describedby` with the element that has keyboard focus, not its ancestor.

---

## 5. Limitations

- `skipDelayDuration` is accepted but not implemented.
- No "group" hover behaviour (tooltip remains after cursor moves to the tooltip bubble) — tooltip disappears on `mouseLeave` from the wrapper span.
- No portal rendering — tooltip bubble is `position: absolute` inside the wrapper span.
