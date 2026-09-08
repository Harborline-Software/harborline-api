# HoverCard — Semantic Contract

- **Component:** HoverCard
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./HoverCard.Interaction.md) · [Accessibility](./HoverCard.Accessibility.md) · [Styling](./HoverCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Radix HoverCard)
- **Catalog row:** #A2 HoverCard (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix @radix-ui/react-hover-card baseline)

---

## 1. Component purpose

**HoverCard** — a card overlay that appears when a user hovers over a trigger element. Used for previewing supplemental information without requiring a click. Automatically dismisses when the user moves the pointer away from both the trigger and the card.

---

## 2. Compound component API (planned)

```typescript
// Root — manages open state
interface HoverCardProps {
  defaultOpen?: boolean
  open?: boolean
  onOpenChange?: (open: boolean) => void
  openDelay?: number    // ms before opening; default: 700
  closeDelay?: number   // ms before closing; default: 300
}

// Trigger — wraps the hover target
interface HoverCardTriggerProps extends React.HTMLAttributes<HTMLElement> {
  asChild?: boolean
}

// Content — the card panel
interface HoverCardContentProps {
  side?: 'top' | 'right' | 'bottom' | 'left'   // default: 'bottom'
  align?: 'start' | 'center' | 'end'            // default: 'center'
  sideOffset?: number                             // default: 4
  className?: string
  children?: React.ReactNode
}
```

---

## 3. Controlled / uncontrolled

Uncontrolled with `defaultOpen`. Controlled with `open` + `onOpenChange`.

---

## 4. Portal rendering

Content renders via a portal into `document.body` to escape overflow/stacking-context constraints.
