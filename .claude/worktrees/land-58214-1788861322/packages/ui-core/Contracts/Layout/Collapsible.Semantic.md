# Collapsible — Semantic Contract

- **Component:** Collapsible
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Collapsible.Interaction.md) · [Accessibility](./Collapsible.Accessibility.md) · [Styling](./Collapsible.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Collapsible.tsx`
- **Catalog row:** #A17 Collapsible (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `preset="panel"`

---

## 1. Component purpose

**Collapsible** — a headless expand/collapse container. A trigger controls whether content is shown or hidden. Lower-level than Accordion — does not group multiple sections; suitable for single-section toggle patterns.

Two operating modes:

- **Headless** (default, `preset` absent): compound-component API (`<Collapsible>`, `<CollapsibleTrigger>`, `<CollapsibleContent>`). No visual chrome — callers compose their own trigger and content styling.
- **Panel preset** (`preset="panel"`): opinionated styled panel with title, subtitle, chevron indicator, header-actions slot. Absorbs the deprecated **ExpansionPanel** component.

---

## 2. Headless API

```typescript
// Root — manages open state
interface CollapsibleProps {
  open?: boolean
  defaultOpen?: boolean
  onOpenChange?: (open: boolean) => void
  disabled?: boolean
  className?: string
  children?: React.ReactNode
}

// Trigger — the toggle button
interface CollapsibleTriggerProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  asChild?: boolean
}

// Content — conditionally visible content
interface CollapsibleContentProps {
  className?: string
  children?: React.ReactNode
}
```

---

## 3. Panel preset API (`preset="panel"`)

```typescript
interface CollapsibleProps {
  preset: 'panel'
  title: string
  subtitle?: string
  open?: boolean               // controlled
  defaultOpen?: boolean        // default: false
  onOpenChange?: (open: boolean) => void
  disabled?: boolean
  headerActions?: React.ReactNode  // rendered before the chevron; stops propagation
  children?: React.ReactNode
  className?: string
}
```

When `preset="panel"` is set, the `CollapsibleTrigger` / `CollapsibleContent` sub-components are not used. The panel chrome is rendered internally.

---

## 4. Controlled / uncontrolled

Controlled when `open` is provided + `onOpenChange` is wired. Uncontrolled when `defaultOpen` seeds initial state (default: `false`).

---

## 5. Content rendering

Content is conditionally rendered (`{isOpen && <div>...}`) — NOT hidden with `display:none`. Unmounts when closed.

---

## 6. headerActions (panel preset)

The `headerActions` slot wraps its children in a `<div onClick={e => e.stopPropagation()}>` — clicks inside headerActions do not toggle the panel.

---

## 7. ExpansionPanel migration

`ExpansionPanel` is a passthrough shim over `Collapsible preset="panel"`. See [ExpansionPanel.Semantic.md](./ExpansionPanel.Semantic.md).
