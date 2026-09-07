# Tooltip — Semantic Contract

- **Component:** Tooltip
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Tooltip.Interaction.md) · [Accessibility](./Tooltip.Accessibility.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled positioned `<div>` (no Radix Tooltip primitive)

---

## 1. Component purpose

**Tooltip** — a hover/focus-triggered inline tooltip. Wraps any `children` in a relative span and shows a `role="tooltip"` label after a configurable delay on hover or focus.

---

## 2. Props

```typescript
type TooltipSide = 'top' | 'right' | 'bottom' | 'left'

interface TooltipProps {
  content: string               // required; tooltip text
  side?: TooltipSide            // default: 'top'
  delayDuration?: number        // default: 700ms; show delay
  skipDelayDuration?: number    // reserved; M1 not used
  children: React.ReactNode     // required; the trigger element
  className?: string            // applied to the tooltip label span
}
```

---

## 3. Content type

`content` is string-only in M1. Rich content (icons, links) inside the tooltip is not supported.

---

## 4. skipDelayDuration

Accepted but not used in M1. Reserved for future "already hovered recently → skip delay" behavior.

---

## 5. Trigger binding — aria-describedby injection

For AT to announce the tooltip text, `aria-describedby` pointing to the tooltip `id` must be present on the **trigger element itself**, not on the wrapper `<span>`.

**Required pattern:**

```typescript
const tooltipId = useId()

// Inject onto the child element via cloneElement:
const trigger = React.cloneElement(
  React.Children.only(children) as React.ReactElement,
  { 'aria-describedby': isVisible ? tooltipId : undefined }
)

// Tooltip bubble:
<span id={tooltipId} role="tooltip" ...>{content}</span>
```

Requires `children` to be a single React element whose DOM root accepts `aria-describedby`. Alternatively, use Radix `<Slot>` to merge the prop without adding a DOM node.

**Do NOT** place `aria-describedby` on the outer wrapper span — AT resolves it from the focused element, not its ancestor.
